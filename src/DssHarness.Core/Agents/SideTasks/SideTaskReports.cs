using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// What the two side-task commands answer: the listing of <c>list-side-tasks</c>, the one document of a one-shot
/// <c>run-side-task --json</c>, and the stopped form each answers where it stops before its turn. The records' directory
/// is named on every exit, in text and in the document.
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
    /// The one document of a one-shot side-task: the task and its records, how the turn stood, what served it, what it
    /// used and cost, its turns and calls, its denials, its final text and its exit code.
    /// </summary>
    /// <param name="task">The side-task's name.</param>
    /// <param name="records">The side-task's records.</param>
    /// <param name="result">How the turn ended.</param>
    /// <param name="denials">The turn's denials, as they were said; empty where none were.</param>
    /// <param name="turns">The side-task's turns, this one's included.</param>
    /// <param name="exitCode">The exit code the command answers.</param>
    public static string One(string task, string records, AgentRunResult result, IReadOnlyList<string> denials, IReadOnlyList<SideTaskTurn> turns, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(denials);
        ArgumentNullException.ThrowIfNull(turns);

        var last = turns is [_, ..] whole ? whole[^1] : null;

        return new JsonObject
        {
            ["task"] = task,
            ["records"] = records,
            ["status"] = result.Status.ToString(),
            ["client"] = last?.Client,
            ["provider"] = last?.Provider,
            ["model"] = result.Served is [_, ..] served ? served[0].Model : result.Model,
            ["served"] = new JsonArray([.. result.Served.Select(one => (JsonNode?)one.Model)]),
            ["usage"] = result.Usage is null ? null : Usage(result.Usage.Total),
            ["cost"] = result.Usage?.Cost?.Usd is { } cost ? Math.Round(cost, 4) : null,
            ["turns"] = result.Turns,
            ["toolCalls"] = result.ToolCalls,
            ["filesWritten"] = new JsonArray([.. result.Files.Select(file => (JsonNode?)file)]),
            ["webRequests"] = result.WebRequests,
            ["denials"] = new JsonArray([.. denials.Select(denial => (JsonNode?)denial)]),
            ["finalText"] = result.FinalText,
            ["exitCode"] = exitCode,
        }.ToJsonString(ReportJson.Options);
    }

    /// <summary>
    /// The stopped form of the side-task commands' documents, where they stop before a turn: the same members, empty,
    /// the exit code first.
    /// </summary>
    /// <param name="exitCode">The exit code the command stops with.</param>
    /// <param name="summary">The line the command stops on.</param>
    public static string Stopped(int exitCode, string summary)
        => new JsonObject
        {
            ["exitCode"] = exitCode,
            ["summary"] = summary,
            ["task"] = null,
            ["records"] = null,
            ["status"] = null,
            ["client"] = null,
            ["provider"] = null,
            ["model"] = null,
            ["served"] = new JsonArray(),
            ["usage"] = null,
            ["cost"] = null,
            ["turns"] = 0,
            ["toolCalls"] = 0,
            ["filesWritten"] = new JsonArray(),
            ["webRequests"] = 0,
            ["denials"] = new JsonArray(),
            ["finalText"] = null,
        }.ToJsonString(ReportJson.Options);

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
