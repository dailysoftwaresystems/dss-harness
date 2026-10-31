namespace DssHarness.Core.Agents;

/// <summary>
/// One attempt of a step, read out of the step's stored stream: the one owner of where an attempt's events begin and
/// end, of the order its turns are numbered in, and of which of its messages are what it was asked and which are what
/// the engine said while it worked - read alike by the continuation record, the compaction, the engine's own handoff
/// and the step that answers what its agent asked.
/// </summary>
/// <remarks>
/// Every attempt appends to the step's one stream, each run of it beginning with a <see cref="RunStarted"/> that names
/// the attempt. An attempt is more than one run where it went on with an answer - a person's answer to what its agent
/// asked, a limit's <c>more</c> or <c>compact</c> - each such run beginning again at turn one; so an attempt's events
/// are those of every run that names it, not those after the newest run alone, and its turns are numbered across them.
/// </remarks>
public static class AgentAttempt
{
    /// <summary>
    /// The events of the newest attempt: every run that names the attempt the stream's last run names, from the first of
    /// them on - the whole stream where no run's beginning was recorded.
    /// </summary>
    /// <param name="stream">The step's stored stream, every attempt's events in order.</param>
    public static IReadOnlyList<AgentEvent> Newest(IReadOnlyList<AgentEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return stream.LastOrDefault(@event => @event is RunStarted) is RunStarted newest ? Of(stream, newest.Attempt) : stream;
    }

    /// <summary>
    /// The events of <paramref name="attempt"/>: every run, after the last run of another attempt, that names it - from
    /// the first of them on; none where the stream's newest runs are another attempt's, and the whole stream where no
    /// run's beginning was recorded.
    /// </summary>
    /// <param name="stream">The step's stored stream, every attempt's events in order.</param>
    /// <param name="attempt">The attempt, from one.</param>
    public static IReadOnlyList<AgentEvent> Of(IReadOnlyList<AgentEvent> stream, int attempt)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var began = -1;
        var runs = false;

        for (var at = 0; at < stream.Count; at++)
        {
            if (stream[at] is RunStarted run)
            {
                runs = true;
                began = run.Attempt != attempt ? -1 : began < 0 ? at : began;
            }
        }

        return !runs ? stream : began < 0 ? [] : [.. stream.Skip(began)];
    }

    /// <summary>
    /// The items of an attempt's events in the stream's order (<see cref="Of"/>): each message of the agent's, and each
    /// message the engine sent while it worked, with the turn of the attempt it falls in - turns numbered across the
    /// attempt's runs, a run's own count from one going on from the last turn of the run before it. The first prompt
    /// of the attempt is what it was asked, not an item; a later run's own first message is an item - the answer it
    /// went on with - but for a compaction record, which stands for the items before it, all of which the attempt's
    /// events hold themselves.
    /// </summary>
    /// <param name="attempt">The attempt's events.</param>
    public static AgentAttemptItems Items(IReadOnlyList<AgentEvent> attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        string? first = null;
        var items = new List<AgentAttemptItem>();
        var answers = new Dictionary<string, ToolCallFinished>(StringComparer.Ordinal);
        var turn = 0;
        var before = 0;

        foreach (var @event in attempt)
        {
            switch (@event)
            {
                case RunStarted:
                    before = turn;
                    break;

                case TurnStarted started:
                    turn = before + started.Turn;
                    break;

                case MessageSent { Which: AgentMessageKind.FirstPrompt } asked when first is null:
                    first = asked.Text;
                    break;

                case MessageSent { Which: AgentMessageKind.FirstPrompt } compacted when compacted.Text.StartsWith(AgentCompaction.SaysWhatItIs, StringComparison.Ordinal):
                    break;

                case MessageSent sent:
                    items.Add(new AgentAttemptItem(turn, Engine: sent));
                    break;

                case ToolCallFinished finished:
                    answers[finished.Id] = finished;
                    break;

                case AssistantMessage message:
                    items.Add(new AgentAttemptItem(turn, Agent: message));
                    break;
            }
        }

        return new AgentAttemptItems(first ?? string.Empty, items, answers);
    }
}

/// <summary>One item of an attempt, in the stream's order: a message of the agent's, or one the engine sent while it worked.</summary>
/// <param name="Turn">The turn of the attempt it falls in, numbered across the attempt's runs.</param>
/// <param name="Agent">The agent's message; null for the engine's.</param>
/// <param name="Engine">The engine's message; null for the agent's.</param>
public sealed record AgentAttemptItem(int Turn, AssistantMessage? Agent = null, MessageSent? Engine = null);

/// <summary>An attempt read as items: what it was first asked, its items in order, and each call's answer by the call's id.</summary>
/// <param name="First">The attempt's first prompt as it was sent: the run context and the task; empty where none was recorded.</param>
/// <param name="Items">Its items, in the stream's order.</param>
/// <param name="Answers">Each call's answer, by the call's id, as the stream holds it.</param>
public sealed record AgentAttemptItems(string First, IReadOnlyList<AgentAttemptItem> Items, IReadOnlyDictionary<string, ToolCallFinished> Answers);
