using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Execution;

namespace DssHarness.Tests;

/// <summary>
/// The stored stream: a step's transcript and there is no other - every event but the deltas, masked, one line each,
/// appended and flushed per line, ending at its last whole event whatever killed its writer.
/// </summary>
public sealed class AgentStreamTests
{
    /// <summary>
    /// The events of a run are stored in the order they happened, each as one line of its own form - its kind, when,
    /// where it falls, and what it carries - with every secret masked.
    /// </summary>
    [Fact]
    public async Task TheEventsOfARun_AreStoredInOrder_Masked_OneLineEach()
    {
        using var temp = new TempDirectory();
        var stream = StoredStream.Open(Step(temp), new Masking("s3cret", "***"));

        await stream.EmitAsync(new SessionStarted("fake", "model-x", "hash", ["Read"], ["skill-a"], HandedConversation: false, SessionId: "s1"));
        await stream.EmitAsync(new MessageSent("the key is s3cret", AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new AssistantMessage("done, holding s3cret", [new AgentToolCall("c1", "Read")]));
        await stream.EmitAsync(new RunFinished());

        stream.Dispose();

        var lines = File.ReadAllLines(Path.Combine(Step(temp), StoredStream.FileName));

        Assert.Equal(4, lines.Length);
        Assert.Equal(new[] { "sessionStarted", "messageSent", "assistantMessage", "runFinished" }, lines.Select(Kind));
        Assert.DoesNotContain("s3cret", File.ReadAllText(Path.Combine(Step(temp), StoredStream.FileName)), StringComparison.Ordinal);
        Assert.Contains("the key is ***", lines[1], StringComparison.Ordinal);
        Assert.Contains("holding ***", lines[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// No delta is stored, and nothing a delta held is missing from the message that follows it: the whole message is
    /// the record, and the file stays readable.
    /// </summary>
    [Fact]
    public async Task NoDeltaIsStored_AndNothingADeltaHeldIsMissingFromTheMessageThatFollows()
    {
        using var temp = new TempDirectory();
        var stream = StoredStream.Open(Step(temp), new Masking("s3cret", "***"));

        await stream.EmitAsync(new TextDelta("the ans"));
        await stream.EmitAsync(new TextDelta("wer is 42"));
        await stream.EmitAsync(new ThinkingDelta("thinking aloud"));
        await stream.EmitAsync(new ToolCallInputDelta("c1", "{\"path\":"));
        await stream.EmitAsync(new AssistantMessage("the answer is 42", [new AgentToolCall("c1", "Read")], "thinking aloud"));
        await stream.EmitAsync(new ToolCallFinished("c1", Ok: true, TimeSpan.FromSeconds(2), "{\"path\": \"a.txt\"}", "the file's text", ResultFile: null));
        await stream.EmitAsync(new RunFinished());

        stream.Dispose();

        var text = File.ReadAllText(Path.Combine(Step(temp), StoredStream.FileName));

        Assert.DoesNotContain("Delta", text, StringComparison.Ordinal);
        Assert.Contains("the answer is 42", text, StringComparison.Ordinal);
        Assert.Contains("thinking aloud", text, StringComparison.Ordinal);
        Assert.Contains("the file's text", text, StringComparison.Ordinal);

        // What an event carries in a list is masked the same: a question's text holds a secret like any text.
        var asking = StoredStream.Open(Step(temp), new Masking("s3cret", "***"));

        await asking.EmitAsync(new PersonAsked([new AgentQuestion("is s3cret the answer?", ["yes", "no"], "no")]));

        asking.Dispose();

        Assert.DoesNotContain("s3cret", File.ReadAllText(Path.Combine(Step(temp), StoredStream.FileName)), StringComparison.Ordinal);
        Assert.Contains("is *** the answer?", File.ReadAllText(Path.Combine(Step(temp), StoredStream.FileName)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A line is flushed before the next event is taken: the file reads whole while its writer is still at work, which
    /// is what a kill leaves behind and what a stalled step's only evidence is.
    /// </summary>
    [Fact]
    public async Task ALineIsFlushedBeforeTheNextEventIsTaken_ReadsWholeWhileTheWriterWorks()
    {
        using var temp = new TempDirectory();
        var stream = StoredStream.Open(Step(temp), new Masking("s3cret", "***"));

        await stream.EmitAsync(new MessageSent("one", AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new AssistantMessage("two", []));

        // A reader of a live stream stands beside its writer: it opens sharing what the writer holds.
        string[] whileOpen;

        using (var beside = new FileStream(Path.Combine(Step(temp), StoredStream.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reading = new StreamReader(beside))
        {
            whileOpen = reading.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        stream.Dispose();

        Assert.Equal(2, whileOpen.Length);
        Assert.Contains("one", whileOpen[0], StringComparison.Ordinal);
        Assert.Contains("two", whileOpen[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A writer killed mid-line leaves the file ending at its last whole event, and one opened again begins there: the
    /// torn line is taken back, whatever it held.
    /// </summary>
    [Fact]
    public async Task ATornLineIsTakenBackOnOpen_AndTheStreamGoesOnFromTheLastWholeEvent()
    {
        using var temp = new TempDirectory();
        var first = StoredStream.Open(Step(temp), new Masking("s3cret", "***"));

        await first.EmitAsync(new MessageSent("one", AgentMessageKind.FirstPrompt));
        await first.EmitAsync(new AssistantMessage("two", []));

        first.Dispose();

        // What a kill leaves: a line begun and never ended.
        File.AppendAllText(Path.Combine(Step(temp), StoredStream.FileName), "{\"kind\": \"assistantMess");

        var again = StoredStream.Open(Step(temp), new Masking("s3cret", "***"));

        await again.EmitAsync(new RunFinished());

        again.Dispose();

        var lines = File.ReadAllLines(Path.Combine(Step(temp), StoredStream.FileName));

        Assert.Equal(new[] { "messageSent", "assistantMessage", "runFinished" }, lines.Select(Kind));
    }

    /// <summary>
    /// A line reads back as its event: the stream is the step's record, and what three things are written from - the
    /// last iterations, a handoff, a compaction - must be able to read it as it was written.
    /// </summary>
    [Fact]
    public async Task ALineReadsBackAsItsEvent()
    {
        using var temp = new TempDirectory();
        var stream = StoredStream.Open(Step(temp), new Masking("s3cret", "***"));

        await stream.EmitAsync(new TurnFinished(3, "model-x", new AgentUsage(10, 5, 0, 0, 1), "end_turn"));
        await stream.EmitAsync(new ToolCallFinished("c1", Ok: true, TimeSpan.FromSeconds(2.5), "{\"path\": \"a.txt\"}", "the file's text", ResultFile: null));

        stream.Dispose();

        var lines = File.ReadAllLines(Path.Combine(Step(temp), StoredStream.FileName));
        var read = System.Text.Json.JsonSerializer.Deserialize<AgentEvent>(lines[0], DssHarness.Core.FileSystem.JsonStateFile.LineOptions);
        var timed = System.Text.Json.JsonSerializer.Deserialize<AgentEvent>(lines[1], DssHarness.Core.FileSystem.JsonStateFile.LineOptions);

        var turn = Assert.IsType<TurnFinished>(read);

        Assert.Equal(3, turn.Turn);
        Assert.Equal("end_turn", turn.StopReason);
        Assert.Equal(10, turn.Usage.InputTokens);
        Assert.Equal("model-x", turn.Model);

        var call = Assert.IsType<ToolCallFinished>(timed);

        Assert.Equal(TimeSpan.FromSeconds(2.5), call.Duration);
        Assert.Equal("the file's text", call.Result);
    }

    /// <summary>
    /// A stored conversation hangs off the same seam: what it began with is written once, and its turns are appended
    /// whole, flushed per line, and recovered to the last whole line after a kill.
    /// </summary>
    [Fact]
    public void AStoredConversation_HangsOffTheSameSeam()
    {
        using var temp = new TempDirectory();
        var layout = DssHarness.Core.Orchestration.OrchestratorLayout.Of(
            new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path), "r1");

        var begun = StoredConversation.Open(layout, "a7", "conv1", "the system prompt", "{\"tools\": []}");

        begun.Append("{\"role\": \"user\", \"text\": \"one\"}");
        begun.Append("{\"role\": \"assistant\", \"text\": \"two\"}");

        begun.Dispose();

        File.AppendAllText(Path.Combine(begun.SessionDirectory, StoredConversation.ConversationFileName), "{\"role\": \"assista");

        var again = StoredConversation.Open(layout, "a7", "conv1", "a different prompt, not written", "{\"tools\": []}");

        again.Append("{\"role\": \"user\", \"text\": \"three\"}");

        again.Dispose();

        var lines = File.ReadAllLines(Path.Combine(layout.AgentSessionDirectory("a7", "conv1"), StoredConversation.ConversationFileName));

        Assert.Equal(3, lines.Length);
        Assert.Contains("one", lines[0], StringComparison.Ordinal);
        Assert.Contains("three", lines[^1], StringComparison.Ordinal);
        Assert.Equal("the system prompt", File.ReadAllText(Path.Combine(layout.AgentSessionDirectory("a7", "conv1"), StoredConversation.SystemFileName)));
    }

    /// <summary>The step's own directory, as the one owner of the paths spells it.</summary>
    private static string Step(TempDirectory temp)
        => Path.Combine(temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1");

    /// <summary>One line's event kind, as its own form names it.</summary>
    private static string Kind(string line) => (string)JsonNode.Parse(line)!["kind"]!;

    /// <summary>A mask that masks one secret, as a run that carries one is given.</summary>
    private sealed class Masking(string secret, string masked) : ILineMask
    {
        public string Redact(string? text) => (text ?? string.Empty).Replace(secret, masked, StringComparison.Ordinal);

        public int Cut(string text) => text.Length;
    }
}
