using System.Globalization;
using System.Text;
using DssHarness.Core.Output;

namespace DssHarness.Core.Agents;

/// <summary>
/// The bounds of the engine's compaction, each with its default (<c>agentContext.compaction</c>).
/// </summary>
/// <param name="ReducedChars">The most characters of the compaction record's reduced part.</param>
/// <param name="CallInputChars">The most characters of a call's input in a reduced line.</param>
public sealed record AgentCompactionBounds(int ReducedChars = AgentCompactionBounds.DefaultReducedChars, int CallInputChars = AgentCompactionBounds.DefaultCallInputChars)
{
    /// <summary>The most characters of the reduced part where <c>reducedChars</c> says none.</summary>
    public const int DefaultReducedChars = 200_000;

    /// <summary>The most characters of a call's input in a reduced line where <c>callInputChars</c> says none.</summary>
    public const int DefaultCallInputChars = 400;

    /// <summary>Where the reduced part is over its bound, the oldest messages of the agent are cut to their first this many characters each, oldest first.</summary>
    public const int OldestMessageChars = 2_000;

    /// <summary>The bounds the configuration gives, each at its default where it says none.</summary>
    /// <param name="config">The <c>agentContext.compaction</c> entry; null where the configuration declares none.</param>
    public static AgentCompactionBounds Of(Configuration.AgentCompactionConfig? config)
        => new(config?.ReducedChars ?? DefaultReducedChars, config?.CallInputChars ?? DefaultCallInputChars);
}

/// <summary>
/// What one compaction made of an attempt: the compaction record itself - the first message of the conversation it
/// begins - and how much of the attempt it kept word for word and reduced, in iterations and in characters.
/// </summary>
/// <param name="Record">The compaction record, whole.</param>
/// <param name="KeptWordForWord">How many of the last iterations it kept word for word.</param>
/// <param name="Reduced">How many iterations it reduced.</param>
/// <param name="SizeBefore">The size of what it reduced, as the stream held it.</param>
/// <param name="SizeAfter">The size of the record it made of it.</param>
public sealed record AgentCompacted(string Record, int KeptWordForWord, int Reduced, long SizeBefore, long SizeAfter);

/// <summary>
/// The engine's compaction (part 03, "Compaction"): a reduction the engine works out from the stored stream, the same
/// input always giving the same output, no model in it - the run context and the task whole as they were sent, the
/// last iterations word for word by the continuation record's rule, every earlier message of the agent its text whole
/// with its thinking left out, every earlier call one line, every earlier result left out, and every message the
/// engine sent since the first whole. It begins a new conversation, on every client: the record is the first message
/// of one, and the old is kept whole and never sent again.
/// </summary>
public static class AgentCompaction
{
    /// <summary>The head of a compaction record, as it says what it is.</summary>
    public const string SaysWhatItIs = "This conversation was compacted by the engine at iteration ";

    /// <summary>
    /// Works the compaction of an attempt out: the record, and how much of the attempt it kept and reduced. The same
    /// events always give the same record. An attempt compacted before is reduced whole all the same: the record that
    /// began its newest conversation stands for the items before it, which the attempt's events hold themselves, so
    /// each record is the attempt's first prompt and its items once, never the records before it again.
    /// </summary>
    /// <param name="attempt">The attempt's events (<see cref="AgentAttempt.Of"/>): every run of it, from its first prompt on.</param>
    /// <param name="continuation">The continuation record's bounds, whose rule and bounds the last iterations are kept by.</param>
    /// <param name="bounds">The compaction's bounds.</param>
    public static AgentCompacted Reduce(IReadOnlyList<AgentEvent> attempt, AgentContinuationBounds continuation, AgentCompactionBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(bounds);

        // The first prompt as it was sent - the run context and the task whole; the calls' answers, joined to the
        // messages that made them; and the attempt's items in the stream's order, the engine's messages among the
        // agent's iterations, each turn numbered across the attempt's runs.
        var read = AgentAttempt.Items(attempt);
        var first = read.First;
        var items = read.Items;
        var answers = read.Answers;
        var sizeBefore = items.Sum(item => (long)(item.Engine?.Text.Length ?? (item.Agent!.Text.Length + (item.Agent.Thinking?.Length ?? 0))))
            + answers.Values.Sum(answer => (long)(answer.Result.Length + answer.Input.Length));

        // The last iterations word for word, by the continuation record's rule; the items before them are the reduced.
        var agents = items.Where(item => item.Agent is not null).ToList();
        var kept = Math.Min(continuation.LastIterations, agents.Count);
        var reducedItems = items.Take(FirstOfTheLast(items, agents, kept)).ToList();
        var wordForWord = AgentContinuation.WordForWord(attempt, continuation with { IterationChars = 0, LastIterations = kept }, copies: null);

        // Where the reduced part is over its bound, the oldest messages of the agent are cut to their first
        // characters each, oldest first, until it fits - and the record says how many were.
        var cut = 0;
        string reduced;

        while (true)
        {
            reduced = Reduced(reducedItems, answers, bounds, cut);

            if (reduced.Length <= bounds.ReducedChars || cut >= reducedItems.Count(item => item.Agent is not null))
            {
                break;
            }

            cut++;
        }

        var written = new StringBuilder();
        written.AppendLine(CultureInfo.InvariantCulture, $"{SaysWhatItIs}{(agents.Count == 0 ? 0 : agents[^1].Turn)}. Everything up to iteration {(reducedItems.Count == 0 ? 0 : reducedItems[^1].Turn)} is reduced to what you wrote and what you called; the last {kept} iterations are word for word.");
        written.AppendLine();
        written.AppendLine("The run context and the task, as they were sent:");
        written.AppendLine();
        written.AppendLine(first.TrimEnd());
        written.AppendLine();

        if (reducedItems.Count > 0)
        {
            written.AppendLine("Reduced, everything up to the last iterations:");
            written.AppendLine();
            written.Append(reduced.TrimEnd());
            written.AppendLine();

            if (cut > 0)
            {
                written.AppendLine();
                written.AppendLine(CultureInfo.InvariantCulture, $"{cut} of the oldest messages were cut to their first {AgentCompactionBounds.OldestMessageChars} characters each.");
            }

            written.AppendLine();
        }

        written.AppendLine("The last iterations, word for word:");
        written.AppendLine();
        written.Append(wordForWord.TrimEnd());
        written.AppendLine();
        written.AppendLine();
        written.AppendLine("The task again:");
        written.AppendLine();
        written.Append(first.TrimEnd());

        var record = written.ToString();

        return new AgentCompacted(record, kept, reducedItems.Count(item => item.Agent is not null), sizeBefore, record.Length);
    }

    /// <summary>The place in <paramref name="items"/> the last <paramref name="kept"/> iterations begin at: everything before it is the reduced part.</summary>
    private static int FirstOfTheLast(IReadOnlyList<AgentAttemptItem> items, List<AgentAttemptItem> agents, int kept)
    {
        if (agents.Count == 0 || kept == 0)
        {
            return items.Count;
        }

        var first = agents[^kept];

        for (var at = 0; at < items.Count; at++)
        {
            if (ReferenceEquals(items[at], first))
            {
                return at;
            }
        }

        return items.Count;
    }

    /// <summary>The reduced part: every earlier message of the agent its text whole (its thinking left out, and its first <paramref name="cut"/> oldest cut at the bound), each call of it one line, every result left out, the engine's messages since the first whole, in the stream's order.</summary>
    private static string Reduced(List<AgentAttemptItem> items, IReadOnlyDictionary<string, ToolCallFinished> answers, AgentCompactionBounds bounds, int cut)
    {
        var written = new StringBuilder();
        var agents = 0;

        foreach (var item in items)
        {
            if (item.Engine is { } engine)
            {
                written.AppendLine(AgentContinuation.EngineSaid);
                written.AppendLine(engine.Text.TrimEnd());
                continue;
            }

            var agent = item.Agent!;
            var oldest = agents < cut;
            var text = oldest ? TextCut.Head(agent.Text, AgentCompactionBounds.OldestMessageChars).TrimEnd() : agent.Text.TrimEnd();
            agents++;

            written.AppendLine(CultureInfo.InvariantCulture, $"iteration {item.Turn}: {text}");

            foreach (var call in agent.Calls)
            {
                var answered = answers.TryGetValue(call.Id, out var finished) ? finished : null;
                var input = answered?.Input ?? string.Empty;

                written.AppendLine(CultureInfo.InvariantCulture, $"  call {call.Id} ({call.Name}): {Input(input, bounds)}{How(answered)}{PathOf(input)}");
            }
        }

        return written.ToString();
    }

    /// <summary>A call's input as a reduced line holds it: cut at the bound.</summary>
    private static string Input(string input, AgentCompactionBounds bounds)
        => input.Length <= bounds.CallInputChars ? input : TextCut.Head(input, bounds.CallInputChars);

    /// <summary>How a call ended, as a reduced line says it: ok, the error's first line, or no result recorded.</summary>
    private static string How(ToolCallFinished? answered)
        => answered is null ? " (no result is recorded)" : answered.Ok ? " (ok)" : $" ({FirstLine(answered.Result)})";

    /// <summary>The paths a call is of, where its input names any - its <c>path</c>, or both ends of a move - read as the engine's account of a stream reads them.</summary>
    private static string PathOf(string input)
        => AgentStreamAccount.Paths(input) switch
        {
            [] => string.Empty,
            [var path] => $" [path: {path}]",
            var paths => $" [paths: {string.Join(", ", paths)}]",
        };

    /// <summary>The first line of a result, as a reduced line quotes it.</summary>
    private static string FirstLine(string result)
    {
        var text = result.TrimStart();
        var line = text.IndexOf('\n');

        return (line > 0 ? text[..line] : text).Trim();
    }
}
