using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// What the two side-task commands answer: the listing of <c>list-side-tasks</c>, the one document of a one-shot
/// <c>run-side-task --json</c>, and the stopped form each answers where it stops before its turn. The records are named on
/// every exit, in text and in the document - the side-task's own directory once it was claimed, and where it was not, a
/// line saying none were written.
/// </summary>
public static class SideTaskReports
{
    /// <summary>The side-tasks' listing, each newest first: its name, when it began and last spoke, its model, its turns and cost, whether it is open now.</summary>
    /// <param name="root">The repository's <c>.side-tasks/</c> directory, named on every exit.</param>
    /// <param name="tasks">The side-tasks, newest began first.</param>
    /// <param name="json">Whether the answer is a document.</param>
    public static CommandOutcome List(string root, IReadOnlyList<(SideTaskLayout Layout, SideTaskRecord Record, bool Open)> tasks, bool json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(tasks);

        if (json)
        {
            var listed = new JsonArray();

            foreach (var (_, record, open) in tasks)
            {
                listed.Add(new JsonObject
                {
                    ["name"] = record.Name,
                    ["began"] = record.Began.ToString("O", CultureInfo.InvariantCulture),
                    ["lastSpoke"] = record.LastSpoke?.ToString("O", CultureInfo.InvariantCulture),
                    ["model"] = record.Turns is [_, ..] turns ? turns[^1].Model : null,
                    ["turns"] = record.Turns.Count,
                    ["costUsd"] = record.InAll?.Cost?.Usd is { } cost ? Math.Round(cost, 4) : null,
                    ["open"] = open,
                    ["records"] = new SideTaskLayout(root, record.Name).Directory,
                });
            }

            var summary = $"{ReportText.Counted(tasks.Count, "side-task")} listed";

            return new CommandOutcome(HarnessExit.Success, summary, [$"Records: {root}"])
            {
                Data = [new JsonObject { ["sideTasks"] = listed, ["exitCode"] = HarnessExit.Success, ["summary"] = summary }.ToJsonString(ReportJson.Options)],
                Quiet = true,
            };
        }

        if (tasks.Count == 0)
        {
            return CommandOutcome.Ok("no side-task of this repository", [$"Records: {root}"]);
        }

        var lines = new List<string>();

        foreach (var (_, record, open) in tasks)
        {
            var model = record.Turns is [_, ..] turns ? turns[^1].Model : "no model yet";
            var cost = record.InAll?.Cost?.Usd is { } usd ? string.Create(CultureInfo.InvariantCulture, $"{usd:0.####} USD") : "no cost said";
            lines.Add($"{record.Name}  {record.Began.LocalDateTime:yyyy-MM-dd HH:mm} - {(record.LastSpoke?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "never spoke")}  {model}  {ReportText.Counted(record.Turns.Count, "turn")}, {cost}{(open ? ", open now" : string.Empty)}");
        }

        return new CommandOutcome(HarnessExit.Success, $"{ReportText.Counted(tasks.Count, "side-task")} listed", [.. lines, $"Records: {root}"]) { Quiet = true };
    }

    /// <summary>
    /// The one document of a one-shot side-task: the exit code and the line the command ends on, then the task and its
    /// records, how the turn stood, what served it, what it used and cost, its turns and calls, its denials and its final
    /// text - the members of <see cref="Empty"/>, filled.
    /// </summary>
    /// <param name="exitCode">The exit code the command answers.</param>
    /// <param name="summary">The line the command ends on.</param>
    /// <param name="task">The side-task's name.</param>
    /// <param name="records">The side-task's records.</param>
    /// <param name="result">How the turn ended.</param>
    /// <param name="denials">The turn's denials, as they were said; empty where none were.</param>
    /// <param name="turn">The turn's own record, which names its client and provider.</param>
    public static string One(int exitCode, string summary, string task, string records, AgentRunResult result, IReadOnlyList<string> denials, SideTaskTurn turn)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(denials);
        ArgumentNullException.ThrowIfNull(turn);

        return ReportJson.Stopped(exitCode, summary, Members(task, records, result, denials, turn));
    }

    /// <summary>
    /// The stopped form of <c>run-side-task</c>'s document, where it stops before its turn has a result: the exit code and
    /// the line first, then the members of <see cref="Empty"/> - the task and its records named where the side-task was
    /// claimed and they were written, null where none were.
    /// </summary>
    /// <param name="exitCode">The exit code the command stops with.</param>
    /// <param name="summary">The line the command stops on.</param>
    /// <param name="task">The side-task's name, where it was claimed; null where it was not.</param>
    /// <param name="records">The side-task's records, where they were written; null where none were.</param>
    public static string Stopped(int exitCode, string summary, string? task = null, string? records = null)
        => ReportJson.Stopped(exitCode, summary, Empty(task, records));

    /// <summary>
    /// The members of <c>run-side-task</c>'s document holding nothing but the task and its records: the one shape its one
    /// document fills and its stopped form leaves empty.
    /// </summary>
    /// <param name="task">The side-task's name; null where none was claimed.</param>
    /// <param name="records">The side-task's records; null where none were written.</param>
    public static JsonObject Empty(string? task = null, string? records = null)
        => Members(task, records, result: null, denials: [], turn: null);

    /// <summary>The members of <c>run-side-task</c>'s document, in their order, from what the side-task holds.</summary>
    private static JsonObject Members(string? task, string? records, AgentRunResult? result, IReadOnlyList<string> denials, SideTaskTurn? turn)
        => new()
        {
            ["task"] = task,
            ["records"] = records,
            ["status"] = result?.Status.ToString(),
            ["client"] = turn?.Client,
            ["provider"] = turn?.Provider,
            ["model"] = result is null ? null : result.Served is [_, ..] served ? served[0].Model : result.Model,
            ["served"] = new JsonArray([.. (result?.Served ?? []).Select(one => (JsonNode?)one.Model)]),
            ["usage"] = result?.Usage is { } usage ? Usage(usage.Total) : null,
            ["cost"] = result?.Usage?.Cost?.Usd is { } cost ? Math.Round(cost, 4) : null,
            ["turns"] = result?.Turns ?? 0,
            ["toolCalls"] = result?.ToolCalls ?? 0,
            ["filesWritten"] = new JsonArray([.. (result?.Files ?? []).Select(file => (JsonNode?)file)]),
            ["webRequests"] = result?.WebRequests ?? 0,
            ["denials"] = new JsonArray([.. denials.Select(denial => (JsonNode?)denial)]),
            ["finalText"] = result?.FinalText,
        };

    /// <summary>
    /// The stopped form of the listing's document, where it stops before it listed anything: the same members, the
    /// listing empty.
    /// </summary>
    /// <param name="exitCode">The exit code the command stops with.</param>
    /// <param name="summary">The line the command stops on.</param>
    public static string ListStopped(int exitCode, string summary)
        => new JsonObject
        {
            ["exitCode"] = exitCode,
            ["summary"] = summary,
            ["sideTasks"] = new JsonArray(),
        }.ToJsonString(ReportJson.Options);

    /// <summary>What a turn used, as the document says it.</summary>
    private static JsonObject Usage(AgentUsage usage)
        => new()
        {
            ["inputTokens"] = usage.InputTokens,
            ["outputTokens"] = usage.OutputTokens,
            ["cacheReadTokens"] = usage.CacheReadTokens,
            ["cacheWriteTokens"] = usage.CacheWriteTokens,
            ["cacheWrite1hTokens"] = usage.CacheWrite1hTokens,
            ["searches"] = usage.Searches,
        };
}
