using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>The exit codes of a session of a run, this command family's own.</summary>
public static class FlowExit
{
    /// <summary>
    /// A question waits: a step's, or the budget's at its reserve. The run's family's own code, which a wrapper that
    /// retries 21 would otherwise loop on an unanswered question.
    /// </summary>
    public const int Paused = 1;
}

/// <summary>
/// What one session of a run ended with, as the run's front end reads it: its status, its questions, what it left
/// open for a person, what it spent, and every record of the run by its path.
/// </summary>
/// <param name="Status">How the run stands: <c>Done</c>, <c>Failed</c>, <c>Paused</c> or <c>Stopped</c>.</param>
/// <param name="Run">The run's name.</param>
/// <param name="Summary">The session in words.</param>
/// <param name="Questions">The questions the session leaves open, in the order raised.</param>
/// <param name="Answered">The questions this session answered, each with who answered and what the engine did.</param>
/// <param name="PassedOver">The instances whose failures the run passed over, in the order they ended.</param>
/// <param name="Handoffs">The handoffs a safe pause wrote, each by its step.</param>
/// <param name="LeftForAPerson">What is left for a person, tied to no step's name: each question still open with its recommendation, and what the run assumed without asking.</param>
/// <param name="Spent">What the run has spent of its budget: usd, tokens and minutes.</param>
/// <param name="ExitCode">The session's exit code.</param>
/// <param name="RunDirectory">The run's directory.</param>
/// <param name="FlowFile">The run's state in one file.</param>
/// <param name="EventsFile">The run's events.</param>
/// <param name="QuestionsFile">The run's questions and answers.</param>
/// <param name="ReportFile">The report this session wrote.</param>
public sealed record FlowRunResult(
    string Status,
    string Run,
    string Summary,
    IReadOnlyList<FlowQuestion> Questions,
    IReadOnlyList<string> Answered,
    IReadOnlyList<string> PassedOver,
    IReadOnlyList<string> Handoffs,
    IReadOnlyList<string> LeftForAPerson,
    FlowBudgetRecord Spent,
    int ExitCode,
    string RunDirectory,
    string FlowFile,
    string EventsFile,
    string QuestionsFile,
    string ReportFile);

/// <summary>
/// The report of a session, <c>report.md</c> and <c>report.json</c>, written by the engine at the end of every
/// session - done, failed, paused or stopped, and once more where it stayed to record after a pause - from the run's
/// records: each step with its state and attempts, the reason of each step that was skipped, not run or passed over,
/// every question with its problem and its answers, each handoff, and what the run has spent of its budget. No step
/// type writes it, so no failed step can stand in front of it.
/// </summary>
public static class FlowReports
{
    /// <summary>Writes the report of a session, markdown and one JSON document beside it.</summary>
    /// <param name="fileSystem">Writes the two files, atomically.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="flow">The run's state in one file, as the session left it.</param>
    /// <param name="questions">The run's questions and answers, as the session left them.</param>
    /// <param name="summary">The session in words.</param>
    /// <param name="exitCode">The session's exit code.</param>
    /// <param name="endedAt">When the session ended.</param>
    /// <returns>What is left for a person, the report's last section, one line each.</returns>
    public static IReadOnlyList<string> Write(
        IFileSystem fileSystem,
        OrchestratorLayout layout,
        FlowFile flow,
        FlowQuestionsFile? questions,
        string summary,
        int exitCode,
        DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        var open = questions?.Open ?? [];
        var left = new List<string>();
        left.AddRange(open.Select(question => $"question {question.Id} ({question.Kind}) is open: {question.ProblemShown?.Happened ?? question.Kind} - answer with answer-orchestrator {layout.Name} <answer> --question {question.Id}"));

        var json = new JsonObject
        {
            ["schema"] = 1,
            ["run"] = flow.Run,
            ["definition"] = flow.Definition,
            ["status"] = flow.Status,
            ["summary"] = summary,
            ["exitCode"] = exitCode,
            ["endedAt"] = endedAt.ToUniversalTime().ToString(FlowIsoTimeConverter.Form, CultureInfo.InvariantCulture),
            ["spent"] = new JsonObject
            {
                ["usd"] = flow.Spent.Usd,
                ["tokens"] = flow.Spent.Tokens,
                ["minutes"] = flow.Spent.Minutes,
            },
            ["steps"] = new JsonArray([.. flow.Steps.Select(StepOf)]),
            ["questions"] = questions is null ? [] : new JsonArray([.. questions.Questions.Select(QuestionOf)]),
            ["leftForAPerson"] = new JsonArray([.. left.Select(line => JsonValue.Create(line))]),
        };

        fileSystem.WriteAllTextAtomic(layout.ReportJsonFile, json.ToJsonString(JsonStateFile.Options) + "\n");
        fileSystem.WriteAllTextAtomic(layout.ReportFile, MarkdownOf(flow, questions, summary, exitCode, left) + "\n");

        return left;
    }

    /// <summary>One step as the report's JSON document holds it.</summary>
    private static JsonObject StepOf(FlowStepRecord step) => new()
    {
        ["instance"] = step.Instance,
        ["state"] = step.State,
        ["attempt"] = step.Attempt,
        ["exit"] = step.Exit,
        ["error"] = step.Error,
        ["skippedBecause"] = step.SkippedBecause,
        ["notRunBecause"] = step.NotRunBecause,
        ["passedOver"] = step.Failure == "passedOver",
    };

    /// <summary>One question as the report's JSON document holds it.</summary>
    private static JsonObject QuestionOf(FlowQuestionEntry question) => new()
    {
        ["id"] = question.Id,
        ["state"] = question.State,
        ["kind"] = question.Kind,
        ["problem"] = question.ProblemShown?.Happened,
        ["answers"] = new JsonArray([.. question.Answers.Select(answer => new JsonObject
        {
            ["answer"] = answer.Answer,
            ["text"] = answer.Text,
            ["by"] = answer.By?.Who,
            ["through"] = answer.By?.Through,
            ["did"] = answer.Did,
        })]),
    };

    /// <summary>The report as a person reads it.</summary>
    private static string MarkdownOf(FlowFile flow, FlowQuestionsFile? questions, string summary, int exitCode, IReadOnlyList<string> left)
    {
        var text = new List<string>
        {
            $"# Run '{flow.Run}' - {flow.Definition}",
            $"{summary} (exit {exitCode.ToString(CultureInfo.InvariantCulture)})",
            string.Empty,
            "## Steps",
        };

        foreach (var step in flow.Steps)
        {
            var said = $"- {step.Instance}: {step.State.ToLower(CultureInfo.InvariantCulture)}";
            var why = step.SkippedBecause ?? step.NotRunBecause ?? (step.Failure == "passedOver" ? "its failure was passed over (onFailure: continue)" : null) ?? step.Error;
            text.Add(why is null ? said : $"{said} - {why}");
        }

        if (questions?.Questions.Count > 0)
        {
            text.Add(string.Empty);
            text.Add("## Questions");

            foreach (var question in questions.Questions)
            {
                text.Add($"- {question.Id} ({question.Kind}, {question.State}): {question.ProblemShown?.Happened ?? question.Kind}");

                foreach (var answer in question.Answers)
                {
                    var who = answer.By is { } by
                        ? by.Who == "person"
                            ? by.Through is { } through ? $"a person, through {through}" : "a person"
                            : by.Who == "configuration" ? $"the configuration, {by.Where}" : by.Who
                        : "someone unknown";
                    text.Add($"  - answered '{answer.Answer}' by {who}{(answer.Text is { } given ? $": \"{given}\"" : string.Empty)}");
                }
            }
        }

        text.Add(string.Empty);
        text.Add("## Spent");
        text.Add($"- {flow.Spent.Usd.ToString("0.00", CultureInfo.InvariantCulture)} USD, {flow.Spent.Tokens.ToString(CultureInfo.InvariantCulture)} tokens, {flow.Spent.Minutes.ToString("0.0", CultureInfo.InvariantCulture)} minutes of reasoning");

        if (left.Count > 0)
        {
            text.Add(string.Empty);
            text.Add("## What is left for a person");
            text.AddRange(left.Select(line => $"- {line}"));
        }

        return string.Join("\n", text);
    }
}
