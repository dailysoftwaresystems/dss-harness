using System.Text.Json;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// Going on with earlier work: the continuation record the engine puts together from the step directories of
/// the agent - its parts in their order, the last iterations first, word for word from the stored stream of the newest
/// attempt, nothing of it cut, its pages by read_result - and the first user message's parts, the run
/// context, the plan updates and the continuation record, as each client is sent them.
/// </summary>
public sealed class AgentContinuationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole record: what it is, then its last iterations word for word, the newest first, then the handoff, the
    /// cut-off message, the last red build or test, the steps so far, the newest first, and the files changed.
    /// </summary>
    [Fact]
    public async Task TheRecord_HoldsItsPartsInTheirOrder()
    {
        var (work, workDirectory) = Work();

        var record = await RenderAsync(work, workDirectory);
        var nl = Environment.NewLine;

        Assert.StartsWith(
            "You are going on with work begun by step 'items[2].implement'. This is a new conversation. What follows is the engine's record of that work, its last iterations first, word for word; the files are as that work left them.",
            record,
            StringComparison.Ordinal);

        var copies = Path.Combine(workDirectory, AgentContinuation.CopiesFolder);
        var newest = $"iteration 2 of 2:{nl}the work stands{nl}{nl}call c-9 (view_file):{nl}  input: {{\"path\":\"a.txt\"}}{nl}  result: alpha{nl}  [the whole result is '{Path.Combine(copies, AgentCallRecords.ToolResultsDirectoryName, "c-9.txt")}']";
        var parts = new[]
        {
            "The last iterations, the newest first:",
            newest,
            "iteration 1 of 2:",
            $"{AgentContinuation.HandoffHeading}:{nl}{nl}Done: the form.",
            AgentContinuation.CutOffMessage,
            "- leg 'windows', phase 'build': the ledger's line" + nl + "  {\"first\":{\"said\":\"msbuild CS0103 src/Program.cs(12,35): The name 'x' does not exist in the current context\"}}",
            "The steps so far, the newest first:",
            "- step 'items[2].implement' ended stopped by a pause.",
            "  Notes: what was done and why",
            "  Output: {\"items\":[1]}",
            "- step 'items[1].plan' ended done.",
            $"The files changed so far, by name:{nl}src/one.cs{nl}src/two.cs",
        };

        var at = 0;

        foreach (var part in parts)
        {
            var found = record.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"'{part}' is not after what came before it in:{nl}{record}");
            at = found + part.Length;
        }

        // Kept whole among the results of the conversation it begins, as read_result reads it.
        Assert.Equal(record, File.ReadAllText(AgentCallRecords.ResultFile(_temp.Combine("new-step"), KeptResults.ContinuationId)));
    }

    /// <summary>An agent that changed none says so; a chain that kept no step says the attempt wrote none.</summary>
    [Fact]
    public async Task WhereNothingWasChangedOrWritten_TheRecordSaysSo()
    {
        var work = new AgentContinuationWork("s-1", [], [], [], CutOff: false);
        var record = await RenderAsync(work, _temp.Combine("work"));

        Assert.Contains("The files: this agent has changed none.", record, StringComparison.Ordinal);
        Assert.Contains("The last iterations: the attempt wrote none.", record, StringComparison.Ordinal);
        Assert.Contains("The steps so far: none.", record, StringComparison.Ordinal);
    }

    /// <summary>
    /// A record of a hundred steps - each with its notes of 2,000 characters and its output, past the 60,000 the record's
    /// own parts were once cut to - lists every step whole, the newest first, every changed path and every iteration: the
    /// step's first message holds what it is, how many pages it is, what each page holds and its first page, and
    /// read_result with the record's id gives every page of it, page 1 first, so every step's notes are read whole.
    /// </summary>
    [Fact]
    public async Task AContinuationRecordOfAHundredSteps_ListsEveryStep_PageOneInThePrompt()
    {
        var steps = new List<AgentContinuedStep>();

        foreach (var each in Enumerable.Range(1, 100))
        {
            var directory = Directory.CreateDirectory(_temp.Combine("chain", $"items[{each}].implement")).FullName;
            File.WriteAllText(Path.Combine(directory, OrchestratorLayout.StepNotesFileName), $"notes of step {each}: " + new string((char)('a' + (each % 26)), 2_000));
            File.WriteAllText(Path.Combine(directory, "output.json"), $$"""{"step":{{each}}}""");
            steps.Add(new AgentContinuedStep($"items[{each}].implement", "done", directory));
        }

        var paths = Enumerable.Range(1, 500).Select(each => $"src/file{each}.cs").ToList();
        var work = new AgentContinuationWork("items[1].implement", steps, paths, [], CutOff: false);
        var stepDirectory = _temp.Combine("new-step");

        var prompt = await RenderAsync(work, _temp.Combine("work"));
        var nl = Environment.NewLine;
        var file = AgentCallRecords.ResultFile(stepDirectory, KeptResults.ContinuationId);
        var whole = File.ReadAllText(file);

        // Three times the 60,000 characters the retired recordChars cut the record's own parts to.
        Assert.True(whole.Length > 180_000, $"the record is {whole.Length} characters");
        Assert.StartsWith("You are going on with work begun by step 'items[1].implement'.", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < whole.Length / 2, "the prompt holds the record's first page, not the record");

        var said = $"The record is ";
        var pages = int.Parse(prompt[(prompt.IndexOf(said, StringComparison.Ordinal) + said.Length)..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"read_result with id '{KeptResults.ContinuationId}' and page gives each of the rest. What each page holds:", prompt, StringComparison.Ordinal);
        Assert.Contains($"- the last iterations: page 1", prompt, StringComparison.Ordinal);
        Assert.Contains($"- the steps so far, the newest first (100 of them): pages ", prompt, StringComparison.Ordinal);
        Assert.Contains($"- the files changed (500 of them): page", prompt, StringComparison.Ordinal);
        Assert.Contains($"page 1 of {pages} · ", prompt, StringComparison.Ordinal);
        Assert.Contains($"ask read_result with id: '{KeptResults.ContinuationId}', page: 2 for the next · the whole is '{file}'", prompt, StringComparison.Ordinal);

        // Every page, read as the new conversation reads it, is the record whole, line by line.
        var results = KeptResults.Of(new PhysicalFileSystem(FilePermissionsFactory.Create()), stepDirectory);
        var lines = new List<string>();

        for (var page = 1; page <= pages; page++)
        {
            var answer = await results.ReadAsync(KeptResults.ContinuationId, page, Token);
            lines.AddRange(answer!.Text.Split('\n').Skip(1));
        }

        Assert.Equal(whole.Split(nl), lines);

        // Every step whole, the newest first; every path.
        var newestAt = whole.IndexOf("- step 'items[100].implement' ended done.", StringComparison.Ordinal);
        var oldestAt = whole.IndexOf("- step 'items[1].implement' ended done.", StringComparison.Ordinal);
        Assert.True(newestAt >= 0 && oldestAt > newestAt);

        foreach (var each in Enumerable.Range(1, 100))
        {
            Assert.Contains($"  Notes: notes of step {each}: " + new string((char)('a' + (each % 26)), 2_000), whole, StringComparison.Ordinal);
        }

        Assert.Contains(string.Join(nl, paths), whole, StringComparison.Ordinal);
    }

    /// <summary>
    /// The newest attempt's iterations are every run of it, numbered as one: an attempt that went on with an answer is
    /// two runs, each counting its turns from one, and the record reads them as the one conversation they are - never
    /// the attempt before it.
    /// </summary>
    [Fact]
    public void TheIterationsOfAnAttemptOfTwoRuns_AreNumberedAcrossThem()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new TurnStarted(1),
            new AssistantMessage("the first attempt", []),
            new RunStarted(2),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("asked", []),
            new RunStarted(2),
            new MessageSent("the person's answer", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("answered", []),
        };

        var told = AgentContinuation.WordForWord(AgentAttempt.Newest(stream), int.MaxValue);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "iteration 1 of 2:",
                "asked",
                string.Empty,
                AgentContinuation.EngineSaid,
                "the person's answer",
                string.Empty,
                "iteration 2 of 2:",
                "answered"),
            told);
    }

    /// <summary>
    /// A later launch of the attempt is sent its first message as every launch is - the run context and, on a new
    /// conversation, the continuation record before the answer as its task - and the record reads the answer alone in
    /// its place: the record the launch carried is never copied into the next, so records never nest.
    /// </summary>
    [Fact]
    public void ALaterLaunchsFirstMessage_IsReadAsItsTask_NeverTheRecordItCarried()
    {
        var launch = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work")) with { RunContext = "run facts", UserPrompt = "do the work" };
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent(AgentPromptParts.FirstMessage(launch, newConversation: true), AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("asked", []),
            new RunStarted(1),
            new MessageSent(AgentPromptParts.FirstMessage(launch with { Continuation = "the record of the launch before", UserPrompt = "the person's answer" }, newConversation: true), AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("answered", []),
        };

        var told = AgentContinuation.WordForWord(AgentAttempt.Newest(stream), int.MaxValue);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "iteration 1 of 2:",
                "asked",
                string.Empty,
                AgentContinuation.EngineSaid,
                "the person's answer",
                string.Empty,
                "iteration 2 of 2:",
                "answered"),
            told);
    }

    /// <summary>
    /// A step that was given its codebase section holds it at the head of its first user message, before the run
    /// context and every other part - the sentence for a graph the run never built held the same way - and the task is
    /// still read out of it alone, whatever the parts before it.
    /// </summary>
    [Fact]
    public void AStepsCodebaseSection_IsTheHeadOfItsFirstMessage_BeforeEveryOtherPart()
    {
        var run = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work")) with { RunContext = "run facts", Continuation = "the work before", UserPrompt = "do the work" };
        var withCodebase = run with { Codebase = "the pack of the step's work" };

        var message = AgentPromptParts.FirstMessage(withCodebase, newConversation: true);
        var nl = Environment.NewLine;

        Assert.StartsWith($"<codebase>{nl}the pack of the step's work{nl}</codebase>{nl}{nl}<{AgentPromptParts.RunContextTag}>", message, StringComparison.Ordinal);
        Assert.EndsWith($"</{AgentPromptParts.ContinuationTag}>{nl}{nl}<{AgentPromptParts.TaskTag}>{nl}do the work{nl}</{AgentPromptParts.TaskTag}>", message, StringComparison.Ordinal);
        Assert.Equal("do the work", AgentPromptParts.TaskOf(message));

        // A graph the run never built says so in the place of the pack, held the same way.
        Assert.StartsWith($"<codebase>{nl}(step 'codebase' left no output: it has not run){nl}</codebase>", AgentPromptParts.FirstMessage(run with { Codebase = "(step 'codebase' left no output: it has not run)" }, newConversation: true), StringComparison.Ordinal);

        // A run given no part is sent its prompt alone, shaped by nothing.
        Assert.Equal("do the work", AgentPromptParts.FirstMessage(run with { RunContext = null, Continuation = null }, newConversation: true));
    }

    /// <summary>
    /// A step that was given the run's repository map holds it first of all, ahead of its codebase section - the same
    /// bytes for every step of the run - and the task is still read out of the message alone, the map a part before it
    /// as the pack is.
    /// </summary>
    [Fact]
    public void AStepsRepositoryMap_IsAheadOfItsCodebaseSection_InItsFirstMessage()
    {
        var run = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work")) with { RunContext = "run facts", UserPrompt = "do the work" };
        var withMap = run with { Map = "the run's repository map", Codebase = "the pack of the step's work" };

        var message = AgentPromptParts.FirstMessage(withMap, newConversation: true);
        var nl = Environment.NewLine;

        Assert.StartsWith($"<repository-map>{nl}the run's repository map{nl}</repository-map>{nl}{nl}<codebase>{nl}the pack of the step's work{nl}</codebase>{nl}{nl}<{AgentPromptParts.RunContextTag}>", message, StringComparison.Ordinal);
        Assert.Equal("do the work", AgentPromptParts.TaskOf(message));

        // A conversation gone on with is sent no map and no pack again: what changed is told in their place, never either whole.
        Assert.StartsWith($"<{AgentPromptParts.RunContextTag}>{nl}", AgentPromptParts.FirstMessage(withMap, newConversation: false), StringComparison.Ordinal);
        Assert.StartsWith(
            $"<codebase-changes>{nl}what changed{nl}</codebase-changes>{nl}{nl}<{AgentPromptParts.RunContextTag}>",
            AgentPromptParts.FirstMessage(withMap with { CodebaseChanges = "what changed" }, newConversation: false),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a launch's conversation is handed is decided entry by entry, so a step carries its map, its pack and the
    /// changes of the codebase together, and the one owner of the parts chooses: a new conversation - a fallback's on
    /// another client or provider among them - is given the map and the pack and never a delta against a conversation it
    /// never had; one gone on with is told the changes alone. What it chose is said the same way to the step's records.
    /// </summary>
    [Fact]
    public void ANewConversation_IsGivenTheMapAndThePack_AndOneGoneOnWithTheChangesAlone()
    {
        var run = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work")) with { UserPrompt = "do the work", Map = "the map", Codebase = "the pack", CodebaseChanges = "what changed" };
        var nl = Environment.NewLine;

        Assert.Equal(
            $"<repository-map>{nl}the map{nl}</repository-map>{nl}{nl}<codebase>{nl}the pack{nl}</codebase>{nl}{nl}<task>{nl}do the work{nl}</task>",
            AgentPromptParts.FirstMessage(run, newConversation: true));
        Assert.Equal(
            $"<codebase-changes>{nl}what changed{nl}</codebase-changes>{nl}{nl}<task>{nl}do the work{nl}</task>",
            AgentPromptParts.FirstMessage(run, newConversation: false));
        Assert.Equal(new AgentCodebaseGiven(Map: true, Codebase: true, Changes: false), AgentPromptParts.CodebaseOf(run, newConversation: true));
        Assert.Equal(new AgentCodebaseGiven(Map: false, Codebase: false, Changes: true), AgentPromptParts.CodebaseOf(run, newConversation: false));

        // A compaction's record is a launch's first message whole: it holds none of them.
        Assert.Equal(AgentCodebaseGiven.None, AgentPromptParts.CodebaseOf(run with { Compaction = "the compaction" }, newConversation: true));
    }

    /// <summary>
    /// Each part of a first message is read back by the one inverse of what writes it, wherever among the parts before the
    /// task it stands - the pack after a repository map as much as a part at the head - and a part whose own text holds
    /// another part's tags is read whole; a part the message does not hold is none.
    /// </summary>
    [Fact]
    public void EachPartOfAFirstMessage_IsReadBack_WhereverItStands()
    {
        var run = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work")) with { UserPrompt = "do the work", Map = "the map", Codebase = "the pack", RunContext = $"facts{Environment.NewLine}</codebase>{Environment.NewLine}{Environment.NewLine}<run-context>{Environment.NewLine}more", Continuation = "the work before" };
        var message = AgentPromptParts.FirstMessage(run, newConversation: true);

        Assert.Equal("the map", AgentPromptParts.PartOf(message, AgentPromptParts.MapTag));
        Assert.Equal("the pack", AgentPromptParts.PartOf(message, AgentPromptParts.CodebaseTag));
        Assert.Equal(run.RunContext, AgentPromptParts.PartOf(message, AgentPromptParts.RunContextTag));
        Assert.Equal("the work before", AgentPromptParts.PartOf(message, AgentPromptParts.ContinuationTag));
        Assert.Null(AgentPromptParts.PartOf(message, AgentPromptParts.CodebaseChangesTag));
        Assert.Equal("do the work", AgentPromptParts.TaskOf(message));

        // A message given no part holds none, however its own text reads.
        Assert.Null(AgentPromptParts.PartOf("<codebase>\nnot a part\n</codebase>", AgentPromptParts.CodebaseTag));
    }

    /// <summary>
    /// On a provider told the envelope in words - Z.AI - each launch is sent its first message as the parts and then the
    /// envelope in a message of its own: the later launch's first message is read as its answer alone and the envelope
    /// said again is no new message of the engine's, so the record holds neither the record the launch carried nor the
    /// schema twice.
    /// </summary>
    [Fact]
    public void ALaterLaunchOfAProviderToldTheEnvelopeInWords_IsReadAsItsAnswerAlone()
    {
        var launch = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work")) with { RunContext = "run facts", UserPrompt = "do the work" };
        const string InWords = "Answer with a JSON object of this schema: {}";
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent(AgentPromptParts.FirstMessage(launch, newConversation: true), AgentMessageKind.FirstPrompt),
            new MessageSent(InWords, AgentMessageKind.EnvelopeInWords),
            new TurnStarted(1),
            new AssistantMessage("asked", []),
            new RunStarted(1),
            new MessageSent(AgentPromptParts.FirstMessage(launch with { Continuation = "the record of the launch before", UserPrompt = "the person's answer" }, newConversation: true), AgentMessageKind.FirstPrompt),
            new MessageSent(InWords, AgentMessageKind.EnvelopeInWords),
            new TurnStarted(1),
            new AssistantMessage("answered", []),
        };

        var told = AgentContinuation.WordForWord(AgentAttempt.Newest(stream), int.MaxValue);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "iteration 1 of 2:",
                "asked",
                string.Empty,
                AgentContinuation.EngineSaid,
                "the person's answer",
                string.Empty,
                "iteration 2 of 2:",
                "answered"),
            told);
    }

    /// <summary>
    /// The task of a first message is what the engine put in its task part, whichever parts came before it; a message
    /// sent without parts is its own task, and so is one that only looks like parts.
    /// </summary>
    [Fact]
    public void TheTaskOfAFirstMessage_IsItsTaskPart_ElseTheMessageWhole()
    {
        var bare = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work")) with { UserPrompt = "do <task>it</task>" };

        Assert.Equal("do <task>it</task>", AgentPromptParts.TaskOf(AgentPromptParts.FirstMessage(bare, newConversation: true)));
        Assert.Equal("do <task>it</task>", AgentPromptParts.TaskOf(AgentPromptParts.FirstMessage(bare with { RunContext = "run facts" }, newConversation: true)));
        Assert.Equal("do <task>it</task>", AgentPromptParts.TaskOf(AgentPromptParts.FirstMessage(bare with { PlanUpdates = "what changed" }, newConversation: true)));
        Assert.Equal("do <task>it</task>", AgentPromptParts.TaskOf(AgentPromptParts.FirstMessage(bare with { RunContext = "run facts", PlanUpdates = "what changed", Continuation = "the record" }, newConversation: true)));
        Assert.Equal("<run-context>a note</run-context> with no task", AgentPromptParts.TaskOf("<run-context>a note</run-context> with no task"));
    }

    /// <summary>
    /// What the engine sent the agent while it worked - a plan update told, a repair - stands in its place, after the
    /// message it followed and before the next: the agent read it there, and the record is word for word.
    /// </summary>
    [Fact]
    public void TheEnginesMessages_StandInTheirPlace()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new MessageSent("before anything", AgentMessageKind.EnvelopeInWords),
            new TurnStarted(1),
            new AssistantMessage("working", []),
            new MessageSent("the plan changed", AgentMessageKind.PlanUpdate),
            new TurnStarted(2),
            new AssistantMessage("done", []),
            new MessageSent("fix the output", AgentMessageKind.Repair),
        };

        var told = AgentContinuation.WordForWord(stream, int.MaxValue);

        // The first prompt is the task, and what the engine said before the agent wrote anything is no iteration's.
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "iteration 1 of 2:",
                "working",
                string.Empty,
                AgentContinuation.EngineSaid,
                "the plan changed",
                string.Empty,
                "iteration 2 of 2:",
                "done",
                string.Empty,
                AgentContinuation.EngineSaid,
                "fix the output"),
            told);
    }

    /// <summary>An iteration is the agent's message as it wrote it, its thinking as far as the provider showed it, and each call it made with the tool, its input and its result as the stream holds them.</summary>
    [Fact]
    public void AnIteration_HoldsTheMessageTheThinkingAndEachCall()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new TurnStarted(7),
            new ToolCallStarted("c-1", AgentTools.ViewFile, AgentToolRunner.Harness),
            new AssistantMessage("look", [new AgentToolCall("c-1", AgentTools.ViewFile)], Thinking: "where it is"),
            new ToolCallFinished("c-1", Ok: true, TimeSpan.Zero, """{"path":"a.txt"}""", "alpha", @"C:\run\tool-results\c-1.txt"),
            new AssistantMessage("alone", []),
        };

        var told = AgentContinuation.WordForWord(stream, int.MaxValue);

        Assert.Contains(
            $"iteration 7 of 7:{Environment.NewLine}[thinking]{Environment.NewLine}where it is{Environment.NewLine}[/thinking]{Environment.NewLine}look{Environment.NewLine}{Environment.NewLine}call c-1 (view_file):{Environment.NewLine}  input: {{\"path\":\"a.txt\"}}{Environment.NewLine}  result: alpha{Environment.NewLine}  [the whole result is 'C:\\run\\tool-results\\c-1.txt']",
            told,
            StringComparison.Ordinal);
    }

    /// <summary>The copies the record names are put in the agent's work directory: each step's notes and output, the stream of the newest attempt, and the results the stream names.</summary>
    [Fact]
    public async Task TheCopies_ArePutInTheAgentsWorkDirectory()
    {
        var (work, workDirectory) = Work();
        var results = Path.Combine(work.StepDirectory, AgentCallRecords.ToolResultsDirectoryName);
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "c-9.txt"), "alpha");

        await RenderAsync(work, workDirectory);

        var copies = Path.Combine(workDirectory, AgentContinuation.CopiesFolder);

        Assert.Equal("what was done and why", File.ReadAllText(Path.Combine(copies, "items[2].implement-notes.md")));
        Assert.True(File.Exists(Path.Combine(copies, "items[2].implement-output.json")));
        Assert.True(File.Exists(Path.Combine(copies, StoredStream.FileName)));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(copies, AgentCallRecords.ToolResultsDirectoryName, "c-9.txt")));
    }

    /// <summary>A run given no part sends its prompt as it always did; a run given parts sends them in their order, the continuation record only where the conversation is a new one, the prompt as the task.</summary>
    [Fact]
    public void TheFirstUserMessage_HoldsThePartsInTheirOrder_TheContinuationOnlyOfANewConversation()
    {
        var bare = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work"));

        Assert.Equal(bare.UserPrompt, AgentPromptParts.FirstMessage(bare, newConversation: true));

        var going = bare with
        {
            UserPrompt = "do the work",
            RunContext = "run facts",
            PlanUpdates = "what changed",
            Continuation = "the record of the work",
        };

        Assert.Equal(
            $"<run-context>{Environment.NewLine}run facts{Environment.NewLine}</run-context>{Environment.NewLine}{Environment.NewLine}<plan-updates>{Environment.NewLine}what changed{Environment.NewLine}</plan-updates>{Environment.NewLine}{Environment.NewLine}<continuation>{Environment.NewLine}the record of the work{Environment.NewLine}</continuation>{Environment.NewLine}{Environment.NewLine}<task>{Environment.NewLine}do the work{Environment.NewLine}</task>",
            AgentPromptParts.FirstMessage(going, newConversation: true));
        Assert.Equal(
            $"<run-context>{Environment.NewLine}run facts{Environment.NewLine}</run-context>{Environment.NewLine}{Environment.NewLine}<plan-updates>{Environment.NewLine}what changed{Environment.NewLine}</plan-updates>{Environment.NewLine}{Environment.NewLine}<task>{Environment.NewLine}do the work{Environment.NewLine}</task>",
            AgentPromptParts.FirstMessage(going, newConversation: false));
    }

    /// <summary>The loop sends the parts as its client is sent them: a new conversation the record, a handed conversation none - and the runner writes the first user message to the step's user.md, masked, before the client is taken.</summary>
    [Fact]
    public async Task TheLoopSendsTheParts_AndTheRunnerWritesUserMd_Masked()
    {
        var events = new RecordedEvents();
        var chat = new FakeChat().Answer(FakeChat.Said("done"));
        var step = Path.Combine(_temp.Path, "step");
        var run = AgentRuns.Of(step, _temp.Combine("work"), new Masking("s3cret", "***")) with
        {
            Model = FakeChat.Model,
            Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30)),
            SessionsDirectory = Path.Combine(_temp.Path, "sessions"),
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1")]),
            Entries = new AgentEntries(
                new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Api = "https://api.anthropic.com", Currency = "USD" },
                new AgentModelConfig { Code = FakeChat.Model, MaxOutputTokens = 64000 },
                null),
            UserPrompt = "do the s3cret work",
            RunContext = "run facts",
            PlanUpdates = "what changed",
            Continuation = "the record of the work",
        };

        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var loop = new ToolLoop(
            _harness.FileSystem,
            new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System),
            TimeProvider.System,
            (_, _) => Task.CompletedTask,
            () => 0);

        run = run with { Ground = PolicyGrounds.Of(_temp.Combine("work"), [], [], [], [], []) };
        var result = await loop.RunAsync(run, events, chat, new ToolLoopTerms("claude-api", run.Entries!.Provider, run.Entries!.Model), Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.StartsWith($"<run-context>{Environment.NewLine}run facts", FakeChat.ContentOf(chat.Requests[0].Messages[0]), StringComparison.Ordinal);
        Assert.Contains("<continuation>", FakeChat.ContentOf(chat.Requests[0].Messages[0]), StringComparison.Ordinal);
        Assert.EndsWith($"<task>{Environment.NewLine}do the *** work{Environment.NewLine}</task>", FakeChat.ContentOf(chat.Requests[0].Messages[0]).TrimEnd(), StringComparison.Ordinal);

        // A handed conversation goes on with one more user message of run context and task alone: no record repeated.
        var handed = run with { Conversation = new AgentSessionRef("claude-api", "anthropic", Guid.NewGuid().ToString("D")) };
        var second = new FakeChat().Answer(FakeChat.Said("done"));

        await loop.RunAsync(handed, events, second, new ToolLoopTerms("claude-api", run.Entries!.Provider, run.Entries!.Model), Token);

        Assert.DoesNotContain("<continuation>", FakeChat.ContentOf(second.Requests[0].Messages[0]), StringComparison.Ordinal);
        Assert.StartsWith($"<run-context>{Environment.NewLine}run facts", FakeChat.ContentOf(second.Requests[0].Messages[0]), StringComparison.Ordinal);

        // The runner writes the first user message as it is sent, masked, before the client is taken.
        var client = new Scripted();
        await new AgentRunner([client], _harness.FileSystem, Elevation.User, TimeProvider.System).RunAsync(
            new AgentStepRequest(
                "r1", "a7", "s1", 1, null, null, null, null, null, null, null, "be terse", "do the s3cret thing", null,
                new AgentWorkspace(null, _temp.Combine("work"), _temp.Path, [], _temp.Combine("work"), _temp.Path),
                null, null, new AgentLimits(10, null, null, null), new Masking("s3cret", "***"))
            {
                RunContext = "run facts",
            },
            events: null,
            Token);

        var userMd = Path.Combine(_temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1", "user.md");

        Assert.True(File.Exists(userMd));
        Assert.Equal($"<run-context>{Environment.NewLine}run facts{Environment.NewLine}</run-context>{Environment.NewLine}{Environment.NewLine}<task>{Environment.NewLine}do the *** thing{Environment.NewLine}</task>", File.ReadAllText(userMd));
    }

    /// <summary>The work the record holds, with its steps' records on the disk and the newest attempt's stream written.</summary>
    private (AgentContinuationWork Work, string WorkDirectory) Work()
    {
        var workDirectory = _temp.Combine("work");
        var plan = Directory.CreateDirectory(_temp.Combine("steps", "items[1].plan")).FullName;
        var implement = Directory.CreateDirectory(_temp.Combine("steps", "items[2].implement")).FullName;

        File.WriteAllText(Path.Combine(implement, OrchestratorLayout.StepNotesFileName), "what was done and why");
        File.WriteAllText(Path.Combine(implement, "output.json"), """{"items":[1]}""");
        File.WriteAllText(Path.Combine(implement, OrchestratorLayout.HandoffFileName), "Done: the form.");

        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new TurnStarted(1),
            new ToolCallStarted("c-9", AgentTools.ViewFile, AgentToolRunner.Harness),
            new AssistantMessage("reading", [new AgentToolCall("c-9", AgentTools.ViewFile)]),
            new ToolCallFinished("c-9", Ok: true, TimeSpan.Zero, """{"path":"a.txt"}""", "alpha", Path.Combine(implement, AgentCallRecords.ToolResultsDirectoryName, "c-9.txt")),
            new TurnStarted(2),
            new AssistantMessage("the work stands", [new AgentToolCall("c-9", AgentTools.ViewFile)]),
        };

        using (var writer = File.CreateText(Path.Combine(implement, StoredStream.FileName)))
        {
            foreach (var @event in stream)
            {
                writer.WriteLine(JsonSerializer.SerializeToNode(@event, typeof(AgentEvent), JsonStateFile.LineOptions)!.ToJsonString(JsonStateFile.LineOptions));
            }
        }

        return (
            new AgentContinuationWork(
                "items[2].implement",
                [new AgentContinuedStep("items[1].plan", "done", plan), new AgentContinuedStep("items[2].implement", "stopped by a pause", implement)],
                ["src/one.cs", "src/two.cs"],
                [new AgentRedLeg("windows", "build", "the ledger's line", Digest: "{\"first\":{\"said\":\"msbuild CS0103 src/Program.cs(12,35): The name 'x' does not exist in the current context\"}}")],
                CutOff: true),
            workDirectory);
    }

    /// <summary>Renders <paramref name="work"/> through the builder on the real file system, its record kept among the results of the step <c>new-step</c>.</summary>
    private Task<string> RenderAsync(AgentContinuationWork work, string workDirectory)
    {
        var builder = new AgentContinuation(new PhysicalFileSystem(FilePermissionsFactory.Create()), _harness.Output);

        return builder.RenderAsync(work, workDirectory, _temp.Combine("new-step"), Token);
    }

    /// <summary>A client that answers at once, remembering the run it was given.</summary>
    private sealed class Scripted : IAgentClient
    {
        public string Name => "scripted";

        public AgentClientCapabilities Capabilities { get; } = new(
            Streaming: true, Thinking: false, WritableWhileWorking: false, AgentConversationHandling.Handed, SchemaEnforcedOutput: false,
            AgentToolsOwner.Harness, AgentToolsOwner.Harness, ReportsCost: false, ["low"], ProviderType: "none", ToldModelBy: "the test");

        public AgentRun? Given { get; private set; }

        public Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            Given = run;
            return Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Model: run.Model));
        }
    }
}
