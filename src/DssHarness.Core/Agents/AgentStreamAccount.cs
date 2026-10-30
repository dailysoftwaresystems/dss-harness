using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;

namespace DssHarness.Core.Agents;

/// <summary>
/// The engine's account of a stored stream, written where an agent could not answer for itself: the handoff a safe
/// pause, a limit or a cut-off leaves holds what the stream shows and no more - the files the newest attempt wrote,
/// the last commands it ran and how each ended, and its last messages word for word, as many as the continuation
/// record keeps - beside the steps before it that left notes, named by step and never by path (part 03, "The handoff
/// turn"). Nothing in it is written or shortened by a model, and nothing the engine wrote is given out as the model's
/// own account: the first line says the engine wrote it.
/// </summary>
public static class AgentStreamAccount
{
    /// <summary>The tools whose call wrote a file or a directory, as the one table names them.</summary>
    private static readonly IReadOnlySet<string> WritingTools = new HashSet<string>(
        Runtime.AgentTools.All(new AgentCommandSettings([], 120, 600))
            .Where(tool => tool.Needs == AgentCapability.Write)
            .Select(tool => tool.Name),
        StringComparer.Ordinal);

    /// <summary>
    /// The account of <paramref name="stream"/> - its newest attempt's events alone - for the engine's own handoff:
    /// the files that attempt wrote, the last commands it ran and how each ended, its last messages word for word, and
    /// the steps named in <paramref name="before"/> that left notes. Null where the stream holds nothing to say, for
    /// the handoff then stands on its first line alone.
    /// </summary>
    /// <param name="stream">The step's stored stream, every attempt's events in order.</param>
    /// <param name="before">The steps before this one that left notes, oldest first; each named by step and not by path.</param>
    /// <param name="keepMessages">How many of the agent's last messages the account keeps word for word.</param>
    public static string? Of(IReadOnlyList<AgentEvent> stream, IReadOnlyList<AgentContinuedStep> before, int keepMessages)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(before);

        // The newest attempt's events only, as the continuation record reads them.
        var began = 0;

        for (var at = 0; at < stream.Count; at++)
        {
            if (stream[at] is RunStarted)
            {
                began = at;
            }
        }

        var window = began == 0 ? stream : [.. stream.Skip(began)];

        if (window.Count == 0)
        {
            return null;
        }

        // Each call's answer, joined to the call the stream made it for - the same joining the continuation record's
        // iterations do.
        var answers = new Dictionary<string, (bool Ok, string Input)>(StringComparer.Ordinal);

        foreach (var @event in window)
        {
            if (@event is ToolCallFinished finished)
            {
                answers[finished.Id] = (finished.Ok, finished.Input);
            }
        }

        var written = new List<string>();
        var commands = new List<(string Said, bool Ok)>();

        foreach (var @event in window)
        {
            if (@event is not ToolCallStarted started || !answers.TryGetValue(started.Id, out var answered))
            {
                continue;
            }

            if (started.Tool == Runtime.AgentTools.RunCommand)
            {
                commands.Add((Said(answered.Input), answered.Ok));
            }
            else if (WritingTools.Contains(started.Tool) && Paths(answered.Input) is [_, ..] paths)
            {
                written.AddRange(paths.Select(path => $"{path} ({started.Tool})"));
            }
        }

        var messages = window.OfType<AssistantMessage>().TakeLast(Math.Max(1, keepMessages)).Select(message => message.Text.TrimEnd()).ToList();

        // Every part says what it holds, or that it holds nothing: the next attempt reads the whole account as one
        // truth of where the work stood, and a part left out would read as a part unknown.
        var told = new StringBuilder();
        told.AppendLine(written.Count == 0
            ? "The files the attempt wrote: none."
            : $"The files the attempt wrote:{Environment.NewLine}{string.Join(Environment.NewLine, written.Select(path => $"- {path}"))}");
        told.AppendLine(commands.Count == 0
            ? "The last commands it ran: none."
            : $"The last commands it ran and how each ended:{Environment.NewLine}{string.Join(Environment.NewLine, commands.Select(command => $"- {command.Said} ({(command.Ok ? "ended ok" : "failed")})"))}");
        told.AppendLine(messages.Count == 0
            ? "The agent's last messages: the attempt wrote none."
            : $"The agent's last messages, word for word:{Environment.NewLine}{string.Join($"{Environment.NewLine}{Environment.NewLine}", messages)}");

        if (before is [_, ..] noted)
        {
            told.AppendLine($"The steps before it that left notes: {string.Join(", ", noted.Select(step => $"'{step.Instance}'"))}.");
        }

        return told.ToString().TrimEnd();
    }

    /// <summary>A command's call as a line says it: its program and its arguments, never its input whole.</summary>
    /// <param name="input">The call's input, as the stream holds it masked.</param>
    private static string Said(string input)
    {
        try
        {
            if (JsonNode.Parse(input) is JsonObject { } call
                && call["program"] is JsonValue program && program.GetValueKind() == JsonValueKind.String)
            {
                var args = call["args"] as JsonArray;
                return string.Join(" ", [program!.GetValue<string>(), .. (args ?? []).OfType<JsonValue>().Where(arg => arg.GetValueKind() == JsonValueKind.String).Select(arg => arg!.GetValue<string>())]).TrimEnd();
            }
        }
        catch (JsonException)
        {
            // An input that does not read is said as it stands.
        }

        return input;
    }

    /// <summary>The paths a write's call names: its <c>path</c>, or both ends of a move; none where the input reads no path.</summary>
    /// <param name="input">The call's input, as the stream holds it masked.</param>
    private static IReadOnlyList<string> Paths(string input)
    {
        try
        {
            if (JsonNode.Parse(input) is not JsonObject call)
            {
                return [];
            }

            var named = new List<string>();

            foreach (var member in new[] { "path", "from", "to" })
            {
                if (call[member] is JsonValue path && path.GetValueKind() == JsonValueKind.String)
                {
                    named.Add(path!.GetValue<string>());
                }
            }

            return named.Count == 0 ? [] : [.. named.Distinct(StringComparer.Ordinal)];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
