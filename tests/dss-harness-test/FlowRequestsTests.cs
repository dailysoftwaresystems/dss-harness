using System.Text.Json;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// <c>questions.json</c> and the run's two request files, <c>stop.json</c> and <c>inbox.json</c>: each reads strictly,
/// refuses what it does not recognise, and takes its own shape alone.
/// </summary>
public sealed class FlowRequestsTests
{
    private static FlowQuestionsFile Questions(params FlowQuestionEntry[] entries)
        => new(1, "r1", DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), entries);

    private static FlowQuestionEntry Entry(string id = "q1", string state = "open", string kind = "loopBound", IReadOnlyList<FlowQuestionOption>? options = null)
        => new(
            id,
            state,
            kind,
            DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            [],
            options ?? [new FlowQuestionOption("more", "one more"), new FlowQuestionOption("abort", "the run ends")],
            [],
            RaisedBy: new FlowQuestionRaisedBy("step", "one"),
            ProblemShown: new FlowQuestionProblem("what happened"),
            Recommendation: FlowRecommendation.NotSought);

    /// <summary>The questions file refuses an id that is not q&lt;n&gt;, a state no question stands in, one answer alone, and one id held by two questions.</summary>
    [Fact]
    public void TheQuestionsFileRefusesWhatItCannotRead()
    {
        Assert.Contains("not a question's id", Questions(Entry(id: "1")).Problem(), StringComparison.Ordinal);
        Assert.Contains("not a state a question stands in", Questions(Entry(state: "gone")).Problem(), StringComparison.Ordinal);
        Assert.Contains("takes at least two", Questions(Entry(options: [new FlowQuestionOption("alone", "the only answer")])).Problem(), StringComparison.Ordinal);

        var both = Questions(Entry(id: "q2"), Entry(id: "q2"));

        Assert.Contains("one id holds two questions", both.Problem(), StringComparison.Ordinal);
        Assert.Null(Questions(Entry(), Entry(id: "q2", state: "answered")).Problem());
    }

    /// <summary>A question's times are ISO 8601 to the second with a Z, and the file round-trips through its own serialization.</summary>
    [Fact]
    public void AQuestionsFileRoundTrips_WithIsoTimes()
    {
        var questions = Questions(
            Entry(),
            new FlowQuestionEntry(
                "q2",
                "answered",
                "pause",
                DateTimeOffset.Parse("2026-10-20T11:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
                [],
                [new FlowQuestionOption("stay", "stay here"), new FlowQuestionOption("go", "go on", Text: true)],
                [new FlowAnswerRecord(
                    "go",
                    DateTimeOffset.Parse("2026-10-20T11:31:00Z", System.Globalization.CultureInfo.InvariantCulture),
                    "split it",
                    new FlowAnswerBy("person", Through: "answer-orchestrator"),
                    "the step ended done")],
                RaisedBy: new FlowQuestionRaisedBy("step", "holding"),
                ProblemShown: new FlowQuestionProblem("what now?", "asked once already", "the rest of the flow waits"),
                Recommendation: FlowRecommendation.NotSought));

        var read = JsonSerializer.Deserialize<FlowQuestionsFile>(JsonSerializer.Serialize(questions, DssHarness.Core.FileSystem.JsonStateFile.Options), DssHarness.Core.FileSystem.JsonStateFile.Options);

        Assert.NotNull(read);
        Assert.Null(read.Problem());
        Assert.Equal(["q1"], read.Open.Select(question => question.Id));
        Assert.Equal("go", read.Of("q2")!.Answers[0].Answer);
        Assert.Equal("answer-orchestrator", read.Of("q2")!.Answers[0].By!.Through);
        Assert.Contains("2026-10-20T11:30:00Z", JsonSerializer.Serialize(questions, DssHarness.Core.FileSystem.JsonStateFile.Options), StringComparison.Ordinal);
    }

    /// <summary>The stop file asks for one thing at a time: a pause and a stop together is refused, and so is nothing.</summary>
    [Fact]
    public void TheStopFileAsksForOneThingAtATime()
    {
        var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Null(new FlowStopFile(1, new FlowStopPause(false, at)).Problem());
        Assert.Null(new FlowStopFile(1, Stop: new FlowStopStop(at)).Problem());
        Assert.Contains("asks for a pause and a stop at once", new FlowStopFile(1, new FlowStopPause(false, at), new FlowStopStop(at)).Problem(), StringComparison.Ordinal);
        Assert.Contains("asks for nothing", new FlowStopFile(1).Problem(), StringComparison.Ordinal);
        Assert.Contains("schema is 2", new FlowStopFile(2, new FlowStopPause(false, at)).Problem(), StringComparison.Ordinal);
    }

    /// <summary>The inbox holds answers on their way to the engine, each naming a question and an answer; anything else is refused.</summary>
    [Fact]
    public void TheInboxHoldsAnswersOnTheirWayToTheEngine()
    {
        var at = DateTimeOffset.Parse("2026-10-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var inbox = new FlowInboxFile(1, [new FlowInboxAnswer("q1", "more", at)]);

        Assert.Null(inbox.Problem());
        Assert.Contains("not a question's id", new FlowInboxFile(1, [new FlowInboxAnswer("one", "more", at)]).Problem(), StringComparison.Ordinal);
        Assert.Contains("answer is empty", new FlowInboxFile(1, [new FlowInboxAnswer("q1", "", at)]).Problem(), StringComparison.Ordinal);
        Assert.Contains("schema is 3", new FlowInboxFile(3, []).Problem(), StringComparison.Ordinal);
    }

    /// <summary>What each answer does is said in one sentence, and no answer is made up.</summary>
    [Fact]
    public void EachAnswerDoesIsSaidInOneSentence()
    {
        Assert.Equal("one more of what was bounded", FlowAnswerDoes.Of("more"));
        Assert.Equal("the run ends as a hard stop ends it", FlowAnswerDoes.Of("abort"));
        Assert.Equal("what this answer does for its question's kind", FlowAnswerDoes.Of("something-else"));
    }
}
