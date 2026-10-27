using System.Globalization;
using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;

namespace DssHarness.Core.Agents;

/// <summary>
/// The bounds of the continuation record, each with its default (<c>agentContext.continuation</c>): how much of
/// earlier work an agent needs is not the same for every repository, so each is a key and not a constant; the rule
/// that nothing is cut in the middle and that whatever is cut is named is not.
/// </summary>
/// <param name="RecordChars">The most characters of the record's own parts, all but the last iterations.</param>
/// <param name="StepChars">The most characters of one step's notes and of its output, each its own bound.</param>
/// <param name="Paths">The most changed paths listed, the count of the rest said.</param>
/// <param name="LastIterations">How many of the last iterations are kept whole, whatever their size.</param>
/// <param name="IterationChars">The most characters of the earlier iterations kept, none cut in the middle of one.</param>
public sealed record AgentContinuationBounds(
    int RecordChars = 60_000,
    int StepChars = 16_000,
    int Paths = 300,
    int LastIterations = 3,
    int IterationChars = 120_000)
{
    /// <summary>The bounds the configuration gives, each at its default where it says none.</summary>
    /// <param name="config">The <c>agentContext.continuation</c> entry; null where the configuration declares none.</param>
    public static AgentContinuationBounds Of(Configuration.AgentContinuationConfig? config)
        => new(
            config?.RecordChars ?? 60_000,
            config?.StepChars ?? 16_000,
            config?.Paths ?? 300,
            config?.LastIterations ?? 3,
            config?.IterationChars ?? 120_000);
}

/// <summary>One step of the chain a continuation record holds: what it was, how it ended, and where its records are.</summary>
/// <param name="Instance">The step instance's name, as the run names it.</param>
/// <param name="Ended">How it ended, in the record's own words: <c>done</c>, <c>stopped by a pause</c>, <c>reached its turn limit</c>, <c>cut off</c>.</param>
/// <param name="Directory">The step's own directory, its <c>notes.md</c> and <c>output.json</c> read and copied from there.</param>
public sealed record AgentContinuedStep(string Instance, string Ended, string Directory);

/// <summary>One red leg of the agent's newest red build or test, as the record says it.</summary>
/// <param name="Leg">The leg's name.</param>
/// <param name="Phase">The phase that was red.</param>
/// <param name="LedgerLine">The ledger's line for it.</param>
/// <param name="Where">Where the copied ledger and its logs are.</param>
public sealed record AgentRedLeg(string Leg, string Phase, string LedgerLine, string Where);

/// <summary>
/// The work a continuation record is the record of, as the engine resolved it: which step goes on, the chain of steps
/// before it (oldest first), the paths this agent's work changed, the red legs of its newest red build or test, and
/// whether the attempt it goes on from was cut off.
/// </summary>
/// <param name="GoingOn">The instance the work going on was begun by.</param>
/// <param name="Steps">The steps so far, oldest first; the last of them is the work's own, its stream and handoff read.</param>
/// <param name="ChangedPaths">The paths this agent's folds wrote and its worktree holds unfolded.</param>
/// <param name="RedLegs">The red legs of the newest red build or test; empty where none was red.</param>
/// <param name="CutOff">Whether the attempt it goes on from was cut off.</param>
public sealed record AgentContinuationWork(
    string GoingOn,
    IReadOnlyList<AgentContinuedStep> Steps,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<AgentRedLeg> RedLegs,
    bool CutOff)
{
    /// <summary>The work's own step directory, its stream and handoff read from; empty where the chain holds none.</summary>
    public string StepDirectory => Steps.Count == 0 ? string.Empty : Steps[^1].Directory;
}

/// <summary>
/// The continuation record (part 03, "Going on with earlier work"): text the engine puts together each time from the
/// step directories of the agent - what it is, the steps so far, the files changed, the last red build or test, the
/// handoff, and the last iterations word for word from the stored stream of the newest attempt - nothing in it written
/// or shortened by a model, whatever is cut named with a file that holds it whole, the copies put in the agent's own
/// <c>continuation/</c> so the record never points into the run's records.
/// </summary>
public sealed class AgentContinuation(IFileSystem fileSystem, Output.IHarnessOutput output)
{
    /// <summary>The folder, in the agent's work directory, the copies a record names are put in.</summary>
    public const string CopiesFolder = "continuation";

    /// <summary>The handoff's heading, as the record gives it its own.</summary>
    public const string HandoffHeading = "The handoff";

    /// <summary>What the record says after an attempt that was cut off.</summary>
    public const string CutOffMessage =
        "The run was interrupted. The tree holds whatever was written before the interruption, and a command that was cut off part way must be checked before it is run again.";

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly OrchestrationStore _store = new(fileSystem ?? throw new ArgumentNullException(nameof(fileSystem)), output ?? throw new ArgumentNullException(nameof(output)));

    /// <summary>
    /// The continuation record of <paramref name="work"/>: its parts in their order, the last iterations of the newest
    /// attempt read from the going-on step's stored stream, every part that was cut named with the copy of it put in
    /// <paramref name="workDirectory"/>'s <c>continuation/</c> - each step's notes and output, the stream of the newest
    /// attempt, and the results the iterations name.
    /// </summary>
    /// <param name="work">The work, as the engine resolved it.</param>
    /// <param name="workDirectory">The agent's work directory, its copies put under it.</param>
    /// <param name="bounds">The record's bounds.</param>
    /// <returns>The record, whole.</returns>
    public string Render(AgentContinuationWork work, string workDirectory, AgentContinuationBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        ArgumentNullException.ThrowIfNull(bounds);

        var copies = Path.Combine(workDirectory, CopiesFolder);
        Copy(work, copies);

        // Part 1: what it is.
        var part1 = $"You are going on with work begun by step '{work.GoingOn}'. This is a new conversation. What follows is the engine's record of that work, ending with its last iterations word for word; the files are as that work left them.";

        // Parts 2 to 4, 6 and 7: the steps so far, the files changed, the last red build or test, the handoff (never
        // cut) and, after an attempt that was cut off, the continuation message - the record's own parts, held to
        // their bound so the last iterations are never crowded out.
        var files = Files(work.ChangedPaths, bounds);
        var handoff = Read(Path.Combine(work.StepDirectory, OrchestratorLayout.HandoffFileName))?.TrimEnd();
        var fixedCost = part1.Length + files.Length + work.RedLegs.Sum(leg => leg.Leg.Length + leg.Phase.Length + leg.LedgerLine.Length + leg.Where.Length + 96)
            + (handoff?.Length ?? 0) + (work.CutOff ? CutOffMessage.Length : 0) + 512;
        var steps = Steps(work, copies, bounds, Math.Max(0, bounds.RecordChars - fixedCost));

        var written = new StringBuilder();
        written.AppendLine(part1);
        written.AppendLine();
        written.AppendLine("The steps so far, oldest first:");
        written.AppendLine();
        written.Append(steps);
        written.AppendLine();
        written.AppendLine(work.ChangedPaths.Count == 0
            ? "The files: this agent has changed none."
            : $"The files changed so far, by name:{Environment.NewLine}{files}");
        written.AppendLine();

        if (work.RedLegs.Count > 0)
        {
            written.AppendLine("The last build or test in this worktree was red:");
            written.AppendLine();

            foreach (var leg in work.RedLegs)
            {
                written.AppendLine(Invariant($"- leg '{leg.Leg}', phase '{leg.Phase}': {leg.LedgerLine} (the ledger and its logs: {leg.Where})"));
            }

            written.AppendLine();
        }

        if (handoff is { Length: > 0 })
        {
            written.AppendLine($"{HandoffHeading}:");
            written.AppendLine();
            written.AppendLine(handoff);
            written.AppendLine();
        }

        if (work.CutOff)
        {
            written.AppendLine(CutOffMessage);
            written.AppendLine();
        }

        // Part 5: the last iterations, word for word, last of all.
        return written.ToString().TrimEnd() + Environment.NewLine + Environment.NewLine
            + Iterations(Stream(work), bounds, copies);
    }

    /// <summary>The steps so far as the record holds them, oldest first: the newest whole whatever it costs, older steps while there is room, the oldest dropped to their copies' names.</summary>
    private string Steps(AgentContinuationWork work, string copies, AgentContinuationBounds bounds, int budget)
    {
        var rendered = new List<(AgentContinuedStep Step, string Text)>();

        foreach (var step in work.Steps)
        {
            var notes = Read(Path.Combine(step.Directory, OrchestratorLayout.StepNotesFileName));
            var output = Read(Path.Combine(step.Directory, "output.json"));

            var text = $"step '{step.Instance}' ended {step.Ended}."
                + (notes is null ? string.Empty : $"{NL}  Notes: {Within(notes, bounds.StepChars, Copy(copies, step, "notes.md"))}")
                + (output is null ? string.Empty : $"{NL}  Output: {Within(output, bounds.StepChars, Copy(copies, step, "output.json"))}");

            rendered.Add((step, text));
        }

        // What is over the bound is given up by the oldest steps; the newest step's notes and output go in whole up to
        // their own bound, so it is never dropped, only cut.
        while (rendered.Count > 1 && rendered.Sum(each => each.Text.Length) + rendered.Count * 4 > budget)
        {
            rendered.RemoveAt(0);
        }

        var written = new StringBuilder();

        if (rendered.Count < work.Steps.Count)
        {
            var leftOut = work.Steps.Count - rendered.Count;

            written.AppendLine(Invariant($"{leftOut} older step{(leftOut == 1 ? string.Empty : "s")} left out here; each one's notes and output are in '{copies}'"));
        }

        foreach (var (_, text) in rendered)
        {
            written.AppendLine($"- {text}");
        }

        return written.ToString();
    }

    /// <summary>The files changed, listed, cut at the bound of paths with the count of the rest said; empty where there are none.</summary>
    private static string Files(IReadOnlyList<string> paths, AgentContinuationBounds bounds)
    {
        if (paths.Count == 0)
        {
            return string.Empty;
        }

        var listed = paths.Take(bounds.Paths).ToList();

        return string.Join(NL, listed)
            + (paths.Count > bounds.Paths ? $"{NL}... and {Invariant(paths.Count - bounds.Paths)} more, not listed" : string.Empty);
    }

    /// <summary>Puts the copies the record names into <paramref name="copies"/>: each step's notes and output, the stream of the newest attempt, and the results the stream names - replaced by the next record, never pointing into the run's records.</summary>
    private void Copy(AgentContinuationWork work, string copies)
    {
        foreach (var step in work.Steps)
        {
            Copied(Path.Combine(step.Directory, OrchestratorLayout.StepNotesFileName), Copy(copies, step, "notes.md"));
            Copied(Path.Combine(step.Directory, "output.json"), Copy(copies, step, "output.json"));
        }

        if (work.StepDirectory.Length == 0)
        {
            return;
        }

        Copied(Path.Combine(work.StepDirectory, StoredStream.FileName), Path.Combine(copies, StoredStream.FileName));

        foreach (var line in _store.ReadStepStream(work.StepDirectory))
        {
            if (line is ToolCallFinished { ResultFile: { } file } && Path.GetFileName(file) is { Length: > 0 } name)
            {
                Copied(file, Path.Combine(copies, AgentCallRecords.ToolResultsDirectoryName, name));
            }
        }
    }

    /// <summary>Copies <paramref name="from"/> to <paramref name="to"/> where it is there, making the directory; what is not there names nothing.</summary>
    private void Copied(string from, string to)
    {
        if (_fileSystem.FileExists(from))
        {
            _fileSystem.CreateDirectory(Path.GetDirectoryName(to)!);
            _fileSystem.WriteAllTextAtomic(to, _fileSystem.ReadAllText(from));
        }
    }

    /// <summary>A text read whole, or null where its file is not there.</summary>
    private string? Read(string file)
        => _fileSystem.FileExists(file) ? _fileSystem.ReadAllText(file) : null;

    /// <summary>A text as the record holds it: whole where it is within <paramref name="most"/>, else cut at its last whole line and named with the copy that holds it whole.</summary>
    private static string Within(string text, int most, string copy)
    {
        if (text.Length <= most)
        {
            return text;
        }

        var lines = Lines(text, most);

        return $"{lines}{NL}  [... {Invariant(text.Length - lines.Length)} characters left out: the whole is '{copy}']";
    }

    /// <summary>A text's leading whole lines within <paramref name="most"/> characters.</summary>
    private static string Lines(string text, int most)
    {
        var head = TextCut.Head(text, most);
        var cut = head.LastIndexOf('\n');

        if (cut <= 0)
        {
            return head;
        }

        // A line ends at its newline, and on Windows before it a carriage return ends it too.
        return head[..(head[cut - 1] == '\r' ? cut - 1 : cut)];
    }

    /// <summary>The name of a step's copy in the agent's <c>continuation/</c>: its instance, made one a file name holds, and the file's own name.</summary>
    private static string Copy(string copies, AgentContinuedStep step, string file)
        => Path.Combine(copies, $"{step.Instance.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_')}-{file}");

    /// <summary>The work's stored stream, every attempt's events in order; none where the work kept no step.</summary>
    private IReadOnlyList<AgentEvent> Stream(AgentContinuationWork work)
        => work.StepDirectory.Length == 0 ? [] : _store.ReadStepStream(work.StepDirectory);

    /// <summary>A number as the record says it.</summary>
    private static string Invariant(int count) => count.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string NL => Environment.NewLine;

    /// <summary>
    /// The last iterations of a stored stream, word for word: the last <see cref="AgentContinuationBounds.LastIterations"/>
    /// whole whatever their size, then as many earlier ones as fit in <see cref="AgentContinuationBounds.IterationChars"/>,
    /// counted backwards and never cut in the middle of one - each the agent's message as it wrote it, its thinking as
    /// far as the provider showed it, and each call it made with the tool, its input and its result as the stream holds
    /// them, oldest first, each marked with its place, and the copy of the stream named where earlier ones were left out.
    /// </summary>
    /// <param name="stream">The stored stream, every attempt's events in order.</param>
    /// <param name="bounds">The record's bounds.</param>
    /// <param name="copies">The directory the copy the left-out iterations are named by.</param>
    public static string Iterations(IReadOnlyList<AgentEvent> stream, AgentContinuationBounds bounds, string copies)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentException.ThrowIfNullOrWhiteSpace(copies);

        // The newest attempt's events only: the stream holds every attempt's, each beginning with its RunStarted.
        var began = 0;

        for (var at = 0; at < stream.Count; at++)
        {
            if (stream[at] is RunStarted)
            {
                began = at;
            }
        }

        // One iteration per assistant message: its text, its thinking, and each call it made answered by the stream -
        // a call's answer arrives after the message that made it, so the answers are collected first and joined to it.
        var answers = new Dictionary<string, (bool Ok, string Input, string Result, string? File)>(StringComparer.Ordinal);
        var turn = 0;

        for (var at = began; at < stream.Count; at++)
        {
            switch (stream[at])
            {
                case TurnStarted started:
                    turn = started.Turn;
                    break;

                case ToolCallFinished finished:
                    answers[finished.Id] = (finished.Ok, finished.Input, finished.Result, finished.ResultFile);
                    break;
            }
        }

        var iterations = new List<(int Turn, string Text)>();
        turn = 0;

        for (var at = began; at < stream.Count; at++)
        {
            switch (stream[at])
            {
                case TurnStarted started:
                    turn = started.Turn;
                    break;

                case AssistantMessage message:
                {
                    var one = new StringBuilder();

                    if (message.Thinking is { Length: > 0 } thinking)
                    {
                        one.Append("[thinking]").Append(NL).Append(thinking.TrimEnd()).Append(NL).Append("[/thinking]").Append(NL);
                    }

                    one.Append(message.Text.TrimEnd());

                    foreach (var call in message.Calls)
                    {
                        one.Append(NL).Append(NL).Append("call ").Append(call.Id).Append(" (").Append(call.Name).Append("):");

                        if (answers.TryGetValue(call.Id, out var answered))
                        {
                            one.Append(answered.Ok ? string.Empty : " failed")
                                .Append(NL).Append("  input: ").Append(answered.Input)
                                .Append(NL).Append("  result: ").Append(answered.Result)
                                .Append(answered.File is { } file ? $"{NL}  [the whole result is '{file}']" : string.Empty);
                        }
                        else
                        {
                            one.Append(NL).Append("  no result is recorded");
                        }
                    }

                    iterations.Add((turn, one.ToString()));
                    break;
                }
            }
        }

        if (iterations.Count == 0)
        {
            return "The last iterations: the attempt wrote none.";
        }

        // The last ones whole, then as many earlier ones as fit, counted backwards, never cut in the middle of one.
        var whole = Math.Min(bounds.LastIterations, iterations.Count);
        var included = new List<(int Turn, string Text)>();
        var budget = bounds.IterationChars;

        for (var from = iterations.Count - 1; from >= 0; from--)
        {
            var lastOne = iterations.Count - from <= whole;

            if (lastOne || iterations[from].Text.Length <= budget)
            {
                included.Insert(0, iterations[from]);

                if (!lastOne)
                {
                    budget -= iterations[from].Text.Length;
                }
            }
        }

        var told = new StringBuilder();
        var leftOut = iterations.Count - included.Count;

        if (leftOut > 0)
        {
            told.Append(Invariant($"[{leftOut} earlier iteration{(leftOut == 1 ? string.Empty : "s")} left out: the stream of the attempt is in '{Path.Combine(copies, StoredStream.FileName)}']")).Append(NL).Append(NL);
        }

        var lastTurn = iterations[^1].Turn;

        foreach (var iteration in included)
        {
            told.Append(Invariant($"iteration {iteration.Turn} of {lastTurn}:")).Append(NL);
            told.Append(iteration.Text.TrimEnd()).Append(NL).Append(NL);
        }

        return told.ToString().TrimEnd();
    }
}

/// <summary>
/// The parts of a step's first user message (part 03, "Prompts" and "What each client is sent"), and the one owner of
/// their order: the run context, the plan updates, the continuation record - only where the conversation is a new one,
/// which is every conversation on Claude Code and a fallback's or a compaction's on the API clients - and the step's
/// filled prompt as the task. A run given no part sends its prompt as it always did, shaped by nothing.
/// </summary>
public static class AgentPromptParts
{
    /// <summary>The run context's tag, as the first user message wraps it.</summary>
    public const string RunContextTag = "run-context";

    /// <summary>The plan updates' tag, between the run context and the continuation record.</summary>
    public const string PlanUpdatesTag = "plan-updates";

    /// <summary>The continuation record's tag.</summary>
    public const string ContinuationTag = "continuation";

    /// <summary>The task's tag, the filled template the run's prompt holds.</summary>
    public const string TaskTag = "task";

    private static string NL => Environment.NewLine;

    /// <summary>
    /// The first user message of <paramref name="run"/>: its parts in their order, the continuation record only where
    /// <paramref name="newConversation"/> says the conversation is a new one, and the run's prompt as the task - the
    /// prompt alone, shaped by nothing, where the run was given no part.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="newConversation">Whether the conversation the run begins is a new one, the continuation record sent only where it is.</param>
    public static string FirstMessage(AgentRun run, bool newConversation)
    {
        ArgumentNullException.ThrowIfNull(run);

        var parts = new List<(string Tag, string Text)>();

        if (run.RunContext is { Length: > 0 } context)
        {
            parts.Add((RunContextTag, context));
        }

        if (run.PlanUpdates is { Length: > 0 } updates)
        {
            parts.Add((PlanUpdatesTag, updates));
        }

        if (newConversation && run.Continuation is { Length: > 0 } continuation)
        {
            parts.Add((ContinuationTag, continuation));
        }

        if (parts.Count == 0)
        {
            return run.UserPrompt;
        }

        var written = new StringBuilder();

        foreach (var (tag, text) in parts)
        {
            written.Append('<').Append(tag).Append('>').Append(NL).Append(text.TrimEnd()).Append(NL).Append("</").Append(tag).Append('>').Append(NL).Append(NL);
        }

        written.Append('<').Append(TaskTag).Append('>').Append(NL).Append(run.UserPrompt.TrimEnd()).Append(NL).Append("</").Append(TaskTag).Append('>');

        return written.ToString();
    }
}
