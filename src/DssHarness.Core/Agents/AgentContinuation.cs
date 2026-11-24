using System.Globalization;
using System.Text;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Agents;

/// <summary>One step of the chain a continuation record holds: what it was, how it ended, and where its records are.</summary>
/// <param name="Instance">The step instance's name, as the run names it.</param>
/// <param name="Ended">How it ended, in the record's own words: <c>done</c>, <c>stopped by a pause</c>, <c>reached its turn limit</c>, <c>cut off</c>.</param>
/// <param name="Directory">The step's own directory, its <c>notes.md</c> and <c>output.json</c> read and copied from there.</param>
public sealed record AgentContinuedStep(string Instance, string Ended, string Directory);

/// <summary>One red leg of the agent's newest red build or test, as the record says it.</summary>
/// <param name="Leg">The leg's name.</param>
/// <param name="Phase">The phase that was red.</param>
/// <param name="LedgerLine">The ledger's line for it.</param>
/// <param name="Digest">
/// What the phase that did not pass printed, digested and said for a model - the first error whole with its stack
/// rendered to the frames inside the tree, the next distinct errors to four, the rest counted - as the red entry
/// holds it; null where the entry carries none.
/// </param>
/// <param name="Tail">
/// The raw lines the red entry carries beside its digest, which says none of them again
/// (<see cref="Results.ResultDigest.Handed"/>): none where the digest holds a problem and says the lines no parser held
/// itself, the phase's last lines where it holds none or there is no digest - so a leg whose output no parser read is
/// never said by its ledger line alone.
/// </param>
/// <param name="Log">
/// The log the phase wrote, whose lines the digest's ranges number, where this machine holds it: the record copies it
/// and names the copy; null where it holds none - a leg a host ran keeps its log there.
/// </param>
public sealed record AgentRedLeg(string Leg, string Phase, string LedgerLine, string? Digest, IReadOnlyList<string>? Tail = null, string? Log = null);

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
/// The continuation record: text the engine puts together each time from the step directories of the agent - what it
/// is, then the last iterations of the newest attempt word for word from its stored stream, the newest first and every
/// one of them, the handoff, the last red build or test, the steps so far, the newest first, each with its notes and its
/// output whole, and every file changed - nothing in it written or shortened by a model, and nothing of it cut. It is
/// kept whole among the results of the conversation it begins (<see cref="KeptResults.ContinuationId"/>), and laid in
/// pages at the room of a tool's answer: the step's first message holds its first page, an index of what each page holds,
/// and how <c>read_result</c> gives every page of it. The copies of what it reads are put in <c>continuation/</c> of what
/// the step's agent is handed (<see cref="Orchestration.OrchestratorLayout.HandedDirectory"/>): a place the agent reads and
/// no command of its writes, so the record never points into the rest of the run's records and nothing the agent planted
/// leads the copies elsewhere.
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
    /// The continuation record of <paramref name="work"/>, written whole among the results of the conversation it begins -
    /// the file <c>read_result</c> reads by <see cref="KeptResults.ContinuationId"/> in <paramref name="stepDirectory"/> -
    /// and what the step's first message holds of it: the record whole where it is one page; else what it is, its pages and
    /// what each holds, how <c>read_result</c> gives each, and its first page. The copies of what it reads are put in
    /// <paramref name="handed"/>'s <c>continuation/</c> - each step's notes and output, each red leg's failing log, the
    /// stream of the newest attempt, and the results the iterations name - each written relative to
    /// <paramref name="handed"/>, held, never through a link.
    /// </summary>
    /// <param name="work">The work, as the engine resolved it.</param>
    /// <param name="handed">Where the step's agent is handed what it reads, its copies put under it.</param>
    /// <param name="stepDirectory">The directory of the step whose conversation the record begins, its results kept there.</param>
    /// <param name="cancellationToken">Stops the laying of its pages.</param>
    /// <returns>What the step's first message holds of the record.</returns>
    public async Task<string> RenderAsync(AgentContinuationWork work, string handed, string stepDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(handed);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepDirectory);

        var copies = Path.Combine(handed, CopiesFolder);
        _fileSystem.CreateDirectory(handed);
        Copy(work, handed, copies);

        var record = new Record();
        var whatItIs = $"You are going on with work begun by step '{work.GoingOn}'. This is a new conversation. What follows is the engine's record of that work, its last iterations first, word for word; the files are as that work left them.";
        record.Part(null, whatItIs);

        // The last iterations, the newest first, every one of them, word for word.
        var iterations = IterationsOf(AgentAttempt.Newest(Stream(work)), copies);

        if (iterations.Count == 0)
        {
            record.Part("the last iterations", "The last iterations: the attempt wrote none.");
        }
        else
        {
            var lastTurn = iterations[^1].Turn;
            record.Part(Invariant($"the last iterations, the newest first (iterations {lastTurn} to {iterations[0].Turn})"), "The last iterations, the newest first:");

            foreach (var (turn, text) in Enumerable.Reverse(iterations))
            {
                record.Part(null, Invariant($"iteration {turn} of {lastTurn}:{NL}{text.TrimEnd()}"));
            }
        }

        if (Read(Path.Combine(work.StepDirectory, OrchestratorLayout.HandoffFileName))?.TrimEnd() is { Length: > 0 } handoff)
        {
            record.Part("the handoff", $"{HandoffHeading}:{NL}{NL}{handoff}");
        }

        if (work.CutOff)
        {
            record.Part("what the interruption left", CutOffMessage);
        }

        if (work.RedLegs.Count > 0)
        {
            // Each red leg by its ledger line, what its phase printed digested, the raw lines the entry carries beside the
            // digest - each line once, as the entry holds them - and its log's copy, whose lines the digest's ranges number.
            record.Part(
                "the last red build or test",
                "The last build or test in this worktree was red:" + NL + NL
                    + string.Join(NL, work.RedLegs.Select(leg =>
                        $"- leg '{leg.Leg}', phase '{leg.Phase}': {leg.LedgerLine}"
                        + (leg.Digest is { Length: > 0 } digested ? NL + "  " + digested : string.Empty)
                        + (leg.Tail is { Count: > 0 } tail ? NL + "  Its lines:" + string.Concat(tail.Select(line => NL + "    " + line)) : string.Empty)
                        + (leg.Log is { } log && _fileSystem.FileExists(log) && LogCopyOf(copies, leg) is { } copy ? NL + $"  Its log, whose lines the digest's ranges number: {copy}" : string.Empty))));
        }

        // The steps so far, the newest first, each with its notes and its output whole.
        record.Part(Invariant($"the steps so far, the newest first ({work.Steps.Count} of them)"), work.Steps.Count == 0 ? "The steps so far: none." : "The steps so far, the newest first:");

        foreach (var step in Enumerable.Reverse(work.Steps))
        {
            var notes = Read(Path.Combine(step.Directory, OrchestratorLayout.StepNotesFileName));
            var output = Read(Path.Combine(step.Directory, "output.json"));

            record.Part(
                null,
                $"- step '{step.Instance}' ended {step.Ended}."
                    + (notes is null ? string.Empty : $"{NL}  Notes: {notes.TrimEnd()}")
                    + (output is null ? string.Empty : $"{NL}  Output: {output.TrimEnd()}"));
        }

        record.Part(
            Invariant($"the files changed ({work.ChangedPaths.Count} of them)"),
            work.ChangedPaths.Count == 0
                ? "The files: this agent has changed none."
                : $"The files changed so far, by name:{NL}{string.Join(NL, work.ChangedPaths)}");

        // Kept whole among the results of the conversation it begins, and laid in pages as every result is: its first page
        // is the one read_result gives first, so the page it gives next goes on where this one ends.
        var whole = record.Text;
        var file = AgentCallRecords.ResultFile(stepDirectory, KeptResults.ContinuationId);
        var bytes = Encoding.UTF8.GetBytes(whole);

        _fileSystem.CreateDirectory(Path.GetDirectoryName(file)!);
        _fileSystem.WriteAtomic(file, stream => stream.Write(bytes));

        if (KeptResults.OnePage(whole))
        {
            return whole;
        }

        var results = KeptResults.Of(_fileSystem, stepDirectory);
        var (kept, _) = await results.LaidAsync(file, PageKey.OfCall(KeptResults.ContinuationId), $"{AgentTools.ReadResult} {KeptResults.ContinuationId}", numbered: false, _fileSystem.OpenRead, cancellationToken).ConfigureAwait(false);

        if (kept.Marks.Count == 1)
        {
            return whole;
        }

        var first = await results.FirstPageAsync(KeptResults.ContinuationId, cancellationToken).ConfigureAwait(false);

        return whatItIs + NL + NL
            + Invariant($"The record is {kept.Marks.Count} pages, every one of it whole: its first page follows, and read_result with id '{KeptResults.ContinuationId}' and page gives each of the rest. What each page holds:") + NL
            + string.Join(NL, record.Index(kept).Select(line => $"- {line}")) + NL + NL
            + first;
    }

    /// <summary>Puts the copies the record names into <paramref name="copies"/>, under <paramref name="handed"/>: each step's notes and output, each red leg's failing log, the stream of the newest attempt, and the results the stream names - replaced by the next record, never pointing into the run's records.</summary>
    private void Copy(AgentContinuationWork work, string handed, string copies)
    {
        foreach (var step in work.Steps)
        {
            Copied(handed, Path.Combine(step.Directory, OrchestratorLayout.StepNotesFileName), Copy(copies, step, "notes.md"));
            Copied(handed, Path.Combine(step.Directory, "output.json"), Copy(copies, step, "output.json"));
        }

        foreach (var leg in work.RedLegs)
        {
            if (leg.Log is { } log && LogCopyOf(copies, leg) is { } copy)
            {
                Copied(handed, log, copy);
            }
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

    /// <summary>Where a red leg's failing log is copied in <c>continuation/</c>, named for its leg and its phase; null where it names no log.</summary>
    private static string? LogCopyOf(string copies, AgentRedLeg leg)
        => leg.Log is null ? null : Execution.PhaseLogs.OfLeg(Path.Combine(copies, OrchestratorLayout.StepLogsDirectoryName), leg.Leg, leg.Phase);

    /// <summary>A text read whole, or null where its file is not there.</summary>
    private string? Read(string file)
        => _fileSystem.FileExists(file) ? _fileSystem.ReadAllText(file) : null;

    /// <summary>The name of a step's copy in <c>continuation/</c>: its instance, made one a file name holds, and the file's own name.</summary>
    private static string Copy(string copies, AgentContinuedStep step, string file)
        => Path.Combine(copies, $"{step.Instance.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_')}-{file}");

    /// <summary>The work's stored stream, every attempt's events in order; none where the work kept no step.</summary>
    private IReadOnlyList<AgentEvent> Stream(AgentContinuationWork work)
        => work.StepDirectory.Length == 0 ? [] : _store.ReadStepStream(work.StepDirectory);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string NL => Environment.NewLine;

    /// <summary>
    /// The last <paramref name="last"/> iterations of an attempt word for word, oldest first - what a compaction keeps of
    /// them (<see cref="AgentCompaction"/>): each the agent's message as it wrote it, its thinking as far as the provider
    /// showed it, each call it made with the tool, its input and its result as the stream holds them, and every message
    /// the engine sent the agent after it, in place, each marked with its turn of the attempt.
    /// </summary>
    /// <param name="attempt">The attempt's events (<see cref="AgentAttempt.Of"/>): every run of it, its turns numbered across them.</param>
    /// <param name="last">How many of the last iterations are given.</param>
    internal static string WordForWord(IReadOnlyList<AgentEvent> attempt, int last)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentOutOfRangeException.ThrowIfNegative(last);

        var iterations = IterationsOf(attempt, copies: null);

        if (iterations.Count == 0)
        {
            return "The last iterations: the attempt wrote none.";
        }

        var lastTurn = iterations[^1].Turn;
        var told = new StringBuilder();

        foreach (var (turn, text) in iterations.TakeLast(last))
        {
            told.Append(Invariant($"iteration {turn} of {lastTurn}:")).Append(NL);
            told.Append(text.TrimEnd()).Append(NL).Append(NL);
        }

        return told.ToString().TrimEnd();
    }

    /// <summary>
    /// Every iteration of an attempt, oldest first, word for word: one per message of the agent's - its text, its
    /// thinking, each call it made answered by the stream - and what the engine said after it, for the agent read that
    /// before its next message, so it belongs where it sits. A call's whole result is named by its copy in
    /// <paramref name="copies"/> where one is given, else by its file.
    /// </summary>
    private static List<(int Turn, string Text)> IterationsOf(IReadOnlyList<AgentEvent> attempt, string? copies)
    {
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
                    var whole = answered.ResultFile is not { } file ? null
                        : copies is null || Path.GetFileName(file) is not { Length: > 0 } name ? file
                        : Path.Combine(copies, AgentCallRecords.ToolResultsDirectoryName, name);

                    one.Append(answered.Ok ? string.Empty : " failed")
                        .Append(NL).Append("  input: ").Append(answered.Input)
                        .Append(NL).Append("  result: ").Append(answered.Result)
                        .Append(whole is null ? string.Empty : $"{NL}  [the whole result is '{whole}']");
                }
                else
                {
                    one.Append(NL).Append("  no result is recorded");
                }
            }

            iterations.Add((item.Turn, one));
        }

        return [.. iterations.Select(iteration => (iteration.Turn, iteration.Text.ToString()))];
    }

    /// <summary>
    /// The record as it is put together: its parts in their order, each a block of lines apart from the next, and where
    /// each named part begins - the line it starts at - so the index can say which pages hold it.
    /// </summary>
    private sealed class Record
    {
        private readonly StringBuilder _text = new();
        private readonly List<(string Name, long Line)> _named = [];
        private long _lines;

        /// <summary>The record whole.</summary>
        public string Text => _text.ToString();

        /// <summary>Adds a part, a blank line before it where one came before, named in the index where it has a name.</summary>
        public void Part(string? name, string text)
        {
            if (_lines > 0)
            {
                _text.Append(NL).Append(NL);
                _lines++;
            }

            if (name is not null)
            {
                _named.Add((name, _lines + 1));
            }

            _text.Append(text);
            _lines += DssHarness.Core.Text.LineEnds.Count(text);
        }

        /// <summary>What each named part's pages are, in the record's order: the first page that holds its first line to the page that holds its last.</summary>
        public IEnumerable<string> Index(TextPagesKept kept)
        {
            for (var at = 0; at < _named.Count; at++)
            {
                var (name, line) = _named[at];
                var end = at + 1 < _named.Count ? _named[at + 1].Line - 2 : kept.Lines;
                var from = TextPages.PageOfLine(kept, Math.Min(line, kept.Lines));
                var to = TextPages.PageOfLine(kept, Math.Clamp(end, line, kept.Lines));

                // A part's last line may go on into the pages after the one it begins on, where it is wider than a page.
                while (to < kept.Marks.Count && kept.Marks[to].FirstLine <= end && kept.Marks[to].FirstColumn > 0)
                {
                    to++;
                }

                yield return from == to ? Invariant($"{name}: page {from}") : Invariant($"{name}: pages {from} to {to}");
            }
        }
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

