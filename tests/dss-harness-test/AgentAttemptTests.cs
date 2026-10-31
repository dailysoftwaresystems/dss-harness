using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Runtime;

namespace DssHarness.Tests;

/// <summary>
/// One attempt of a step read out of its stored stream (<see cref="AgentAttempt"/>): every run that names it - an attempt
/// that went on with an answer is more than one run, each counting its turns from one - its turns numbered across them,
/// its first prompt apart from the engine's later messages, and an earlier compaction record left out for the events it
/// stood for. The one owner the continuation record, the compaction and the engine's handoff read alike.
/// </summary>
public sealed class AgentAttemptTests
{
    /// <summary>
    /// The newest attempt is every run that names it, from the first of them: a compaction or an answer begins a new run
    /// of the same attempt, and what the runs before it did is the attempt's as much as what the last did.
    /// </summary>
    [Fact]
    public void TheNewestAttempt_IsEveryRunThatNamesIt_FromTheFirstOfThem()
    {
        AgentEvent[] stream =
        [
            new RunStarted(1),
            new AssistantMessage("the first attempt", []),
            new RunStarted(2),
            new AssistantMessage("asked", []),
            new RunStarted(2),
            new AssistantMessage("answered", []),
        ];

        Assert.Equal(stream[2..], AgentAttempt.Newest(stream));
        Assert.Equal(stream[2..], AgentAttempt.Of(stream, 2));
    }

    /// <summary>
    /// An attempt another came after has no events of its own any more - a step's stream never goes back to one - and a
    /// stream with no run's beginning recorded is one attempt whole.
    /// </summary>
    [Fact]
    public void AnAttemptAnotherCameAfter_HasNone_AndAStreamOfNoRunIsOneAttempt()
    {
        AgentEvent[] stream = [new RunStarted(1), new AssistantMessage("one", []), new RunStarted(2), new AssistantMessage("two", [])];
        AgentEvent[] bare = [new TurnStarted(1), new AssistantMessage("alone", [])];

        Assert.Empty(AgentAttempt.Of(stream, 1));
        Assert.Equal(bare, AgentAttempt.Newest(bare));
        Assert.Equal(bare, AgentAttempt.Of(bare, 3));
    }

    /// <summary>
    /// An attempt's items: its first prompt apart, the task it was asked; a later run's own first message one of the
    /// engine's, in its place - but an earlier compaction record, which stood for the items the attempt's events hold
    /// themselves; every turn numbered across the runs, each run's count going on from the last turn of the one before.
    /// </summary>
    [Fact]
    public void TheItems_NumberTurnsAcrossRuns_TheTaskApart_AndAnEarlierRecordLeftOut()
    {
        var call = new ToolCallFinished("c-1", Ok: true, TimeSpan.Zero, """{"path":"a.txt"}""", "alpha");
        var read = AgentAttempt.Items(
        [
            new RunStarted(1),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("one", [new AgentToolCall("c-1", AgentTools.ViewFile)]),
            call,
            new TurnStarted(2),
            new AssistantMessage("two", []),
            new RunStarted(1),
            new MessageSent($"{AgentCompaction.SaysWhatItIs}2. the rest", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("three", []),
            new RunStarted(1),
            new MessageSent("the person's answer", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("four", []),
            new MessageSent("the plan changed", AgentMessageKind.PlanUpdate),
        ]);

        Assert.Equal("do the work", read.First);
        Assert.Equal(
            [(1, "one"), (2, "two"), (3, "three"), (3, "the person's answer"), (4, "four"), (4, "the plan changed")],
            read.Items.Select(item => (item.Turn, item.Agent?.Text ?? item.Engine!.Text)));
        Assert.Equal(call, Assert.Single(read.Answers).Value);
    }

    /// <summary>
    /// The engine's own handoff reads the whole attempt: a file written before the agent asked and was answered is one the
    /// attempt wrote, though the run that wrote it is not the attempt's last.
    /// </summary>
    [Fact]
    public void TheEnginesHandoff_NamesTheFilesEveryRunOfTheAttemptWrote()
    {
        AgentEvent[] stream =
        [
            new RunStarted(1),
            new ToolCallStarted("c-1", AgentTools.CreateFile, AgentToolRunner.Harness),
            new AssistantMessage("wrote it", [new AgentToolCall("c-1", AgentTools.CreateFile)]),
            new ToolCallFinished("c-1", Ok: true, TimeSpan.Zero, """{"path":"made.txt","content":"x"}""", "ok"),
            new RunStarted(1),
            new AssistantMessage("went on", []),
        ];

        var account = AgentStreamAccount.Of(stream, [], keepMessages: 1);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "The files the attempt wrote:",
                $"- made.txt ({AgentTools.CreateFile})",
                "The last commands it ran: none.",
                "The agent's last messages, word for word:",
                "went on"),
            account);
    }

    /// <summary>
    /// The engine's handoff lists the attempt's last commands, as many as it keeps, and says how many earlier ones it left
    /// out: a long attempt's every command would crowd the handoff it heads.
    /// </summary>
    [Fact]
    public void TheEnginesHandoff_ListsTheLastCommands_AndCountsTheRest()
    {
        var stream = new List<AgentEvent> { new RunStarted(1) };

        for (var each = 1; each <= AgentStreamAccount.LastCommands + 2; each++)
        {
            var id = $"c-{each}";
            stream.Add(new ToolCallStarted(id, AgentTools.RunCommand, AgentToolRunner.Harness));
            stream.Add(new ToolCallFinished(id, Ok: true, TimeSpan.Zero, $$"""{"program":"step","args":["{{each}}"]}""", "ok"));
        }

        var account = AgentStreamAccount.Of(stream, [], keepMessages: 1);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                [
                    "The files the attempt wrote: none.",
                    "The last commands it ran and how each ended:",
                    "- (2 earlier left out)",
                    .. Enumerable.Range(3, AgentStreamAccount.LastCommands).Select(each => $"- step {each} (ended ok)"),
                    "The agent's last messages: the attempt wrote none.",
                ]),
            account);
    }
}
