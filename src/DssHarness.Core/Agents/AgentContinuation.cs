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
    int RecordChars = AgentContinuationBounds.DefaultRecordChars,
    int StepChars = AgentContinuationBounds.DefaultStepChars,
    int Paths = AgentContinuationBounds.DefaultPaths,
    int LastIterations = AgentContinuationBounds.DefaultLastIterations,
    int IterationChars = AgentContinuationBounds.DefaultIterationChars)
{
    /// <summary>The most characters of the record's own parts where <c>recordChars</c> says none.</summary>
    public const int DefaultRecordChars = 60_000;

    /// <summary>The most characters of one step's notes and of its output where <c>stepChars</c> says none.</summary>
    public const int DefaultStepChars = 16_000;

    /// <summary>The most changed paths listed where <c>paths</c> says none.</summary>
    public const int DefaultPaths = 300;

    /// <summary>How many of the last iterations are kept whole where <c>lastIterations</c> says none.</summary>
    public const int DefaultLastIterations = 3;

    /// <summary>The most characters of the earlier iterations kept where <c>iterationChars</c> says none.</summary>
    public const int DefaultIterationChars = 120_000;

    /// <summary>The bounds the configuration gives, each at its default where it says none.</summary>
    /// <param name="config">The <c>agentContext.continuation</c> entry; null where the configuration declares none.</param>
    public static AgentContinuationBounds Of(Configuration.AgentContinuationConfig? config)
        => new(
            config?.RecordChars ?? DefaultRecordChars,
            config?.StepChars ?? DefaultStepChars,
            config?.Paths ?? DefaultPaths,
            config?.LastIterations ?? DefaultLastIterations,
            config?.IterationChars ?? DefaultIterationChars);
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
/// The continuation record: text the engine puts together each time from the
/// step directories of the agent - what it is, the steps so far, the files changed, the last red build or test, the
/// handoff, and the last iterations word for word from the stored stream of the newest attempt - nothing in it written
/// or shortened by a model, whatever is cut named with a file that holds it whole, the copies put in <c>continuation/</c>
/// of what the step's agent is handed (<see cref="Orchestration.OrchestratorLayout.HandedDirectory"/>): a place the agent
/// reads and no command of its writes, so the record never points into the rest of the run's records and nothing the
/// agent planted leads the copies elsewhere.
/// </summary>
public sealed class AgentContinuation(IFileSystem fileSystem, Output.IHarnessOutput output)
{
    /// <summary>The folder, in what the step's agent is handed, the copies a record names are put in.</summary>
    public const string CopiesFolder = "continuation";

    /// <summary>The handoff's heading, as the record gives it its own.</summary>
    public const string HandoffHeading = "The handoff";

    /// <summary>What heads a message the engine sent the agent, where an iteration or a reduced part holds it.</summary>
    public const string EngineSaid = "the engine:";

    /// <summary>What the record says after an attempt that was cut off.</summary>
    public const string CutOffMessage =
        "The run was interrupted. The tree holds whatever was written before the interruption, and a command that was cut off part way must be checked before it is run again.";

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly OrchestrationStore _store = new(fileSystem ?? throw new ArgumentNullException(nameof(fileSystem)), output ?? throw new ArgumentNullException(nameof(output)));

    /// <summary>
    /// The continuation record of <paramref name="work"/>: its parts in their order, the last iterations of the newest
    /// attempt read from the going-on step's stored stream, every part that was cut named with the copy of it put in
    /// <paramref name="handed"/>'s <c>continuation/</c> - each step's notes and output, the stream of the newest attempt,
    /// and the results the iterations name - each written relative to <paramref name="handed"/>, held, never through a link.
    /// </summary>
    /// <param name="work">The work, as the engine resolved it.</param>
    /// <param name="handed">Where the step's agent is handed what it reads, its copies put under it.</param>
    /// <param name="bounds">The record's bounds.</param>
    /// <returns>The record, whole.</returns>
    public string Render(AgentContinuationWork work, string handed, AgentContinuationBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(handed);
        ArgumentNullException.ThrowIfNull(bounds);

        var copies = Path.Combine(handed, CopiesFolder);
        _fileSystem.CreateDirectory(handed);
        Copy(work, handed, copies);

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
            + Iterations(AgentAttempt.Newest(Stream(work)), bounds, copies);
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

    /// <summary>Puts the copies the record names into <paramref name="copies"/>, under <paramref name="handed"/>: each step's notes and output, the stream of the newest attempt, and the results the stream names - replaced by the next record, never pointing into the run's records.</summary>
    private void Copy(AgentContinuationWork work, string handed, string copies)
    {
        foreach (var step in work.Steps)
        {
            Copied(handed, Path.Combine(step.Directory, OrchestratorLayout.StepNotesFileName), Copy(copies, step, "notes.md"));
            Copied(handed, Path.Combine(step.Directory, "output.json"), Copy(copies, step, "output.json"));
        }

        if (work.StepDirectory.Length == 0)
        {
            return;
        }

        Copied(handed, Path.Combine(work.StepDirectory, StoredStream.FileName), Path.Combine(copies, StoredStream.FileName));

        foreach (var line in _store.ReadStepStream(work.StepDirectory))
        {
            if (line is ToolCallFinished { ResultFile: { } file } && Path.GetFileName(file) is { Length: > 0 } name)
            {
                Copied(handed, file, Path.Combine(copies, AgentCallRecords.ToolResultsDirectoryName, name));
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="from"/>, a record of the run's, to <paramref name="to"/> under <paramref name="handed"/> where
    /// it is there, relative to the directories held from <paramref name="handed"/> and made on the way; what is not there
    /// names nothing.
    /// </summary>
    private void Copied(string handed, string from, string to)
    {
        if (_fileSystem.FileExists(from))
        {
            using var read = _fileSystem.OpenRead(from);

            UnfollowedFiles.Write(_fileSystem, handed, to, write => read.CopyTo(write));
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

    /// <summary>The name of a step's copy in <c>continuation/</c>: its instance, made one a file name holds, and the file's own name.</summary>
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
    /// The last iterations of an attempt, word for word: the last <see cref="AgentContinuationBounds.LastIterations"/>
    /// whole whatever their size, then as many earlier ones as fit in <see cref="AgentContinuationBounds.IterationChars"/>,
    /// counted backwards and never cut in the middle of one - the first that does not fit ends them, so what is kept is
    /// one run with nothing missing from it - each the agent's message as it wrote it, its thinking as far as the
    /// provider showed it, each call it made with the tool, its input and its result as the stream holds them, and every
    /// message the engine sent the agent after it, in place, oldest first, each marked with its turn of the attempt, and
    /// the copy of the stream named where earlier ones were left out.
    /// </summary>
    /// <param name="attempt">The attempt's events (<see cref="AgentAttempt.Of"/>): every run of it, its turns numbered across them.</param>
    /// <param name="bounds">The record's bounds.</param>
    /// <param name="copies">The directory the copy the left-out iterations are named by.</param>
    public static string Iterations(IReadOnlyList<AgentEvent> attempt, AgentContinuationBounds bounds, string copies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copies);

        return WordForWord(attempt, bounds, copies);
    }

    /// <summary>
    /// The last iterations of an attempt word for word, as <see cref="Iterations(IReadOnlyList{AgentEvent}, AgentContinuationBounds, string)"/>
    /// gives them - with the line naming the copy of the left-out ones only where <paramref name="copies"/> names one: a
    /// compaction holds them reduced above, and says so in its own words.
    /// </summary>
    /// <param name="attempt">The attempt's events.</param>
    /// <param name="bounds">The record's bounds.</param>
    /// <param name="copies">The directory the copy the left-out iterations are named by; null where none is named.</param>
    internal static string WordForWord(IReadOnlyList<AgentEvent> attempt, AgentContinuationBounds bounds, string? copies)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(bounds);

        // One iteration per message of the agent's: its text, its thinking, each call it made answered by the stream,
        // and what the engine said after it - the agent read that before its next message, so it belongs where it sits.
        var read = AgentAttempt.Items(attempt);
        var iterations = new List<(int Turn, StringBuilder Text)>();

        foreach (var item in read.Items)
        {
            if (item.Engine is { } engine)
            {
                // A message the engine sent before the agent wrote anything is part of no iteration.
                if (iterations.Count > 0)
                {
                    iterations[^1].Text.Append(NL).Append(NL).Append(EngineSaid).Append(NL).Append(engine.Text.TrimEnd());
                }

                continue;
            }

            var message = item.Agent!;
            var one = new StringBuilder();

            if (message.Thinking is { Length: > 0 } thinking)
            {
                one.Append("[thinking]").Append(NL).Append(thinking.TrimEnd()).Append(NL).Append("[/thinking]").Append(NL);
            }

            one.Append(message.Text.TrimEnd());

            foreach (var call in message.Calls)
            {
                one.Append(NL).Append(NL).Append("call ").Append(call.Id).Append(" (").Append(call.Name).Append("):");

                if (read.Answers.TryGetValue(call.Id, out var answered))
                {
                    one.Append(answered.Ok ? string.Empty : " failed")
                        .Append(NL).Append("  input: ").Append(answered.Input)
                        .Append(NL).Append("  result: ").Append(answered.Result)
                        .Append(answered.ResultFile is { } file ? $"{NL}  [the whole result is '{file}']" : string.Empty);
                }
                else
                {
                    one.Append(NL).Append("  no result is recorded");
                }
            }

            iterations.Add((item.Turn, one));
        }

        if (iterations.Count == 0)
        {
            return "The last iterations: the attempt wrote none.";
        }

        // The last ones whole, then as many earlier ones as fit, counted backwards, never cut in the middle of one: the
        // first that does not fit ends them, for one kept past it would leave a hole the count below could not name.
        var whole = Math.Min(bounds.LastIterations, iterations.Count);
        var included = new List<(int Turn, string Text)>();
        var budget = bounds.IterationChars;

        for (var from = iterations.Count - 1; from >= 0; from--)
        {
            var text = iterations[from].Text.ToString();
            var lastOne = iterations.Count - from <= whole;

            if (!lastOne && (budget == 0 || text.Length > budget))
            {
                break;
            }

            included.Insert(0, (iterations[from].Turn, text));

            if (!lastOne)
            {
                budget -= text.Length;
            }
        }

        var told = new StringBuilder();
        var leftOut = iterations.Count - included.Count;

        if (leftOut > 0 && copies is not null)
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
/// What of the codebase graph one launch's first message holds, as <see cref="AgentPromptParts"/> decides it: the run's
/// repository map and the step's codebase section - its pack, or the sentence for a graph the run never built - where
/// the launch begins a conversation of its own, or the changes of the codebase where it goes on with a stored one; none of
/// them where it begins from a compaction's record, which is its first message whole.
/// </summary>
/// <param name="Map">Whether the message holds the run's repository map.</param>
/// <param name="Codebase">Whether it holds the step's codebase section.</param>
/// <param name="Changes">Whether it holds the changes of the codebase the conversation it goes on with is told.</param>
public sealed record AgentCodebaseGiven(bool Map, bool Codebase, bool Changes)
{
    /// <summary>A first message that holds none of them: a compaction's record, or one whose run was given no graph.</summary>
    public static AgentCodebaseGiven None { get; } = new(Map: false, Codebase: false, Changes: false);
}

/// <summary>
/// The parts of a step's first user message, and the one owner of their order and of their form: the repository map (the
/// run's own, the same bytes for every step of the run that reads alike, so a client that caches a prefix reads it from
/// the cache) and the codebase section (a reason step's context pack, or the sentence for a graph the run never built) -
/// both only where the conversation is a new one - or, where it goes on with a stored one, the changes of the codebase in
/// their place; then the run context, the plan updates, the continuation record - only where the conversation is a new
/// one, which is every conversation on Claude Code and a fallback's or a compaction's on the API clients - and the step's
/// filled prompt as the task. A run given no part sends its prompt as it always did, shaped by nothing.
/// </summary>
public static class AgentPromptParts
{
    /// <summary>The repository map's tag, as the first user message wraps it: the run's own map, first of all.</summary>
    public const string MapTag = "repository-map";

    /// <summary>The codebase section's tag, as the first user message wraps it: the step's context pack, after the map.</summary>
    public const string CodebaseTag = "codebase";

    /// <summary>The tag of the changes of the codebase a conversation that goes on is told, in the place of a map and a pack.</summary>
    public const string CodebaseChangesTag = "codebase-changes";

    /// <summary>The run context's tag, as the first user message wraps it.</summary>
    public const string RunContextTag = "run-context";

    /// <summary>The plan updates' tag, between the run context and the continuation record.</summary>
    public const string PlanUpdatesTag = "plan-updates";

    /// <summary>The continuation record's tag.</summary>
    public const string ContinuationTag = "continuation";

    /// <summary>The task's tag, the filled template the run's prompt holds.</summary>
    public const string TaskTag = "task";

    private static string NL => Environment.NewLine;

    /// <summary>The tags of the parts a first message may hold before its task, in their order.</summary>
    private static readonly string[] PartTags = [MapTag, CodebaseTag, CodebaseChangesTag, RunContextTag, PlanUpdatesTag, ContinuationTag];

    /// <summary>
    /// What of the codebase graph the first message a launch of <paramref name="run"/> sends holds: none where it begins
    /// from a compaction's record, which is sent whole; else as <see cref="CodebaseOf(string?, string?, string?, bool)"/>
    /// decides it.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="newConversation">Whether the conversation the launch begins is a new one.</param>
    public static AgentCodebaseGiven CodebaseOf(AgentRun run, bool newConversation)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.Compaction is { Length: > 0 } ? AgentCodebaseGiven.None : CodebaseOf(run.Map, run.Codebase, run.CodebaseChanges, newConversation);
    }

    /// <summary>
    /// What of the codebase graph a first message holds, the one decision of it: a new conversation is given the run's map
    /// and the step's codebase section, each where there is one; a conversation gone on with is told what changed of the
    /// codebase, where there is that to tell - never a map or a pack again whole, for its prefix is never changed.
    /// </summary>
    /// <param name="map">The run's repository map; null where the run was given none.</param>
    /// <param name="codebase">The step's codebase section; null where the run was given none.</param>
    /// <param name="changes">The changes of the codebase a stored conversation is told; null where there are none.</param>
    /// <param name="newConversation">Whether the conversation the launch begins is a new one.</param>
    public static AgentCodebaseGiven CodebaseOf(string? map, string? codebase, string? changes, bool newConversation)
        => newConversation
            ? new AgentCodebaseGiven(map is { Length: > 0 }, codebase is { Length: > 0 }, Changes: false)
            : new AgentCodebaseGiven(Map: false, Codebase: false, changes is { Length: > 0 });

    /// <summary>
    /// The first user message of <paramref name="run"/>: its parts in their order - the repository map and the codebase
    /// section where <paramref name="newConversation"/> says the conversation is a new one, the changes of the codebase in
    /// their place where it goes on with a stored one (<see cref="CodebaseOf(string?, string?, string?, bool)"/>), then the
    /// run context, the plan updates and the continuation record, the last only where the conversation is a new one - and
    /// the run's prompt as the task - the prompt alone, shaped by nothing, where the run was given no part.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="newConversation">Whether the conversation the run begins is a new one: the map, the pack and the continuation record are sent only where it is, the changes of the codebase only where it is not.</param>
    public static string FirstMessage(AgentRun run, bool newConversation)
    {
        ArgumentNullException.ThrowIfNull(run);

        var given = CodebaseOf(run.Map, run.Codebase, run.CodebaseChanges, newConversation);
        var parts = new List<(string Tag, string Text)>();

        if (given.Map)
        {
            parts.Add((MapTag, run.Map!));
        }

        if (given.Codebase)
        {
            parts.Add((CodebaseTag, run.Codebase!));
        }

        if (given.Changes)
        {
            parts.Add((CodebaseChangesTag, run.CodebaseChanges!));
        }

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
            written.Append(Part(tag, text));
        }

        written.Append(TaskOpen).Append(run.UserPrompt.TrimEnd()).Append(NL).Append("</").Append(TaskTag).Append('>');

        return written.ToString();
    }

    /// <summary>
    /// One part as a message holds it, the one writer of a part's form: its tags around its text, the text's trailing
    /// blanks gone, and a blank line after - so the part a provider is asked to carry as its own (the map, a cache mark
    /// set on it alone), the parts of a side-task's message and the message the engine records read the same.
    /// </summary>
    /// <param name="tag">The part's tag.</param>
    /// <param name="text">The part's text.</param>
    public static string Part(string tag, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentNullException.ThrowIfNull(text);

        return $"<{tag}>{NL}{text.TrimEnd()}{NL}</{tag}>{NL}{NL}";
    }

    /// <summary>The repository map's part as the first user message holds it (<see cref="Part"/>).</summary>
    /// <param name="map">The run's repository map, whole.</param>
    public static string MapPart(string map)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(map);
        return Part(MapTag, map);
    }

    /// <summary>
    /// The task a first message holds, the inverse of <see cref="FirstMessage"/>: the text of its task part where it was
    /// given parts - every part before it left out (the map, the codebase section or its changes, the run context, the
    /// plan updates and the continuation record), for each is the engine's own record of what the conversation already
    /// holds - and the whole text where it was given none. The parts are read in their order, each ending where the next
    /// part or the task opens, so a task whose own text holds the tags is read whole.
    /// </summary>
    /// <param name="firstMessage">A first user message, as sent.</param>
    public static string TaskOf(string firstMessage)
    {
        ArgumentNullException.ThrowIfNull(firstMessage);

        var close = $"</{TaskTag}>";

        if (Leading(firstMessage) is not { } leading || !firstMessage.EndsWith(close, StringComparison.Ordinal))
        {
            return firstMessage;
        }

        var begins = leading.Task + TaskOpen.Length;

        return begins > firstMessage.Length - close.Length ? firstMessage : firstMessage[begins..^close.Length].Trim();
    }

    /// <summary>
    /// The text of the part named <paramref name="tag"/> in <paramref name="firstMessage"/>, the inverse of what
    /// <see cref="FirstMessage"/> writes for it, wherever among the parts before the task it stands; null where the
    /// message holds none of it.
    /// </summary>
    /// <param name="firstMessage">A first user message, as sent.</param>
    /// <param name="tag">The part's tag.</param>
    public static string? PartOf(string firstMessage, string tag)
    {
        ArgumentNullException.ThrowIfNull(firstMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        foreach (var (named, text) in Leading(firstMessage)?.Parts ?? [])
        {
            if (string.Equals(named, tag, StringComparison.Ordinal))
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>How the task part opens.</summary>
    private static string TaskOpen => $"<{TaskTag}>{NL}";

    /// <summary>
    /// The parts a first message holds before its task, read as <see cref="FirstMessage"/> writes them - each tag in
    /// <see cref="PartTags"/>' order, its text ending at the first close after which the next part or the task opens - and
    /// where the task part begins; null where the message holds no part, or is not of that form.
    /// </summary>
    /// <param name="firstMessage">A first user message, as sent.</param>
    private static (IReadOnlyList<(string Tag, string Text)> Parts, int Task)? Leading(string firstMessage)
    {
        var parts = new List<(string Tag, string Text)>();
        var at = 0;
        var next = 0;

        while (true)
        {
            if (firstMessage.AsSpan(at).StartsWith(TaskOpen, StringComparison.Ordinal))
            {
                return parts.Count > 0 ? (parts, at) : null;
            }

            var from = at;
            var opened = Array.FindIndex(PartTags, next, tag => firstMessage.AsSpan(from).StartsWith($"<{tag}>{NL}", StringComparison.Ordinal));

            if (opened < 0)
            {
                return null;
            }

            var tag = PartTags[opened];
            var body = at + $"<{tag}>{NL}".Length;
            var close = $"{NL}</{tag}>{NL}{NL}";
            var end = -1;

            for (var search = firstMessage.IndexOf(close, body, StringComparison.Ordinal); search >= 0; search = firstMessage.IndexOf(close, search + 1, StringComparison.Ordinal))
            {
                if (Opens(firstMessage, search + close.Length, opened + 1))
                {
                    end = search;
                    break;
                }
            }

            if (end < 0)
            {
                return null;
            }

            parts.Add((tag, firstMessage[body..end]));
            at = end + close.Length;
            next = opened + 1;
        }
    }

    /// <summary>Whether the task, or a part whose tag comes at or after <paramref name="from"/> in their order, opens at <paramref name="at"/>.</summary>
    private static bool Opens(string message, int at, int from)
        => message.AsSpan(at).StartsWith(TaskOpen, StringComparison.Ordinal)
            || PartTags.Skip(from).Any(tag => message.AsSpan(at).StartsWith($"<{tag}>{NL}", StringComparison.Ordinal));
}

