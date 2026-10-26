using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Schemas;

namespace DssHarness.Core.Agents;

/// <summary>
/// The one owner of an answer held to its envelope, on every client (part 03, "Structured output"): whether it reads -
/// its members as the envelope says them (<see cref="AgentEnvelope"/>), its output against the step's own schema, and
/// what no schema can say - how the run ends on one that reads, and the message that sends one back with what did not.
/// A client's own enforcement comes first where it has one; this says what the harness's validator still refuses after
/// it, and holds the answer to the whole of the definition's schema whatever the client was sent.
/// </summary>
/// <remarks>
/// <para>
/// One gate is made for each run that names an output, and counts its repairs. The notes are not empty. The output is
/// null only where the answer asks the person, and an answer with both an output and questions is sent back. Questions
/// are taken only where the envelope offers them - an answer that asks where the step may not, or has asked as often as
/// it may, is sent back told so - and each assumption and question is held to its record's own rules
/// (<see cref="AgentAssumption.Problems"/>, <see cref="AgentQuestion.Problems"/>): every text written, and a question's
/// options no two alike, one of them recommended word for word. A text an <c>enum</c> or a <c>const</c> of the step's
/// schema holds in another case reads as the schema spells it.
/// </para>
/// <para>
/// An answer that reads ends the run: <see cref="AgentRunStatus.Asked"/> where it asks, with its questions, and
/// <see cref="AgentRunStatus.Completed"/> otherwise, with its output; either way with its notes and what it assumed,
/// every text masked.
/// </para>
/// </remarks>
public sealed class StructuredOutputGate
{
    /// <summary>Why an answer that is no JSON object does not read.</summary>
    public const string NoObject = "$: the answer holds no JSON object; answer with one, as the schema says";

    /// <summary>Why an answer that asks does not read where the step may not ask.</summary>
    public const string NobodyAnswers = "$.questions: nobody can answer this step's questions; leave them out, answer with the output, and write what you would have asked under assumptions";

    /// <summary>Why an answer that asks does not read where the step has asked as often as it may.</summary>
    public const string AskedAsOftenAsItMay = "$.questions: the step has asked the person as often as it may; leave them out, answer with the output, and write what you would have asked under assumptions";

    /// <summary>Why an answer with both an output and questions does not read.</summary>
    public const string AskOrAnswer = "$.questions: ask, or answer; an answer with questions has a null output";

    /// <summary>Why an answer whose notes are empty does not read.</summary>
    public const string NotesEmpty = "$.notes: is empty; say what you leave for whoever goes on with this work";

    /// <summary>The shape an assumption has as the envelope says it, which its record's own rule is read on.</summary>
    private static readonly JsonObject AssumptionShape = AgentEnvelope.Assumption();

    /// <summary>The shape a question has as the envelope says it, which its record's own rule is read on.</summary>
    private static readonly JsonObject QuestionShape = AgentEnvelope.Question();

    private readonly AgentOutput _output;
    private readonly JsonObject _step;
    private readonly JsonObject _members;
    private readonly ILineMask _mask;
    private int _repairs;

    /// <summary>The gate of one run's answers.</summary>
    /// <param name="output">The output the run is to produce.</param>
    /// <param name="mask">The run's mask.</param>
    public StructuredOutputGate(AgentOutput output, ILineMask mask)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _mask = mask ?? throw new ArgumentNullException(nameof(mask));
        _step = JsonNode.Parse(output.Schema) as JsonObject ?? throw new ArgumentException("The output's schema is no JSON object.", nameof(output));
        _members = AgentEnvelope.Members(output);
        Envelope = AgentEnvelope.Schema(output);
    }

    /// <summary>The envelope the run's answer is asked in, as JSON (<see cref="AgentEnvelope.Schema"/>).</summary>
    public string Envelope { get; }

    /// <summary>The message that sends an answer back, naming each problem by its path in the answer.</summary>
    /// <param name="problems">What does not read, each one a path and what is wrong there.</param>
    public static string Repair(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        return "Your answer does not read against the schema it was asked for:\n"
            + string.Concat(problems.Select(problem => $"- {problem}\n"))
            + "Answer again, whole, as the schema says.";
    }

    /// <summary>Why <paramref name="answer"/> does not read; empty where it does.</summary>
    /// <param name="answer">The answer, as the client received it; null where it gave none.</param>
    public IReadOnlyList<string> Problems(JsonNode? answer) => Read(answer, string.Empty).Problems;

    /// <summary>
    /// The run's end on <paramref name="answer"/>: <see cref="AgentRunStatus.Completed"/> or
    /// <see cref="AgentRunStatus.Asked"/> where it reads - the person asked said by a <see cref="PersonAsked"/> - and
    /// <see cref="AgentRunStatus.InvalidOutput"/> where it does not and its repairs are spent; else null, the answer sent
    /// back with its problems by <paramref name="sendBack"/>, the repair said by an <see cref="OutputRepair"/>.
    /// </summary>
    /// <param name="answer">The answer, as the client received it; null where it gave none.</param>
    /// <param name="text">The answer's text.</param>
    /// <param name="events">Where the run's events go.</param>
    /// <param name="sendBack">Sends the agent the message a repair is, in the same conversation.</param>
    public async Task<AgentRunResult?> CheckedAsync(JsonNode? answer, string text, IAgentEventSink events, Func<string, Task> sendBack)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sendBack);

        var (problems, ended) = Read(answer, text);

        if (ended is not null)
        {
            if (ended.Status == AgentRunStatus.Asked)
            {
                await events.EmitAsync(new PersonAsked(ended.Questions)).ConfigureAwait(false);
            }

            return ended;
        }

        if (_repairs >= _output.MaxRepairs)
        {
            return AgentEndings.InvalidOutput(_repairs, problems, _mask);
        }

        _repairs++;
        await events.EmitAsync(new OutputRepair(_repairs, [.. problems.Select(_mask.Redact)])).ConfigureAwait(false);
        await sendBack(Repair(problems)).ConfigureAwait(false);

        return null;
    }

    /// <summary>What does not read of <paramref name="answer"/>, or the run's end where it all does.</summary>
    private (IReadOnlyList<string> Problems, AgentRunResult? Ended) Read(JsonNode? answer, string text)
    {
        if (answer is not JsonObject envelope)
        {
            return ([NoObject], null);
        }

        var problems = new List<string>();
        var offered = AgentEnvelope.Offers(_output);
        var members = envelope;

        if (!offered && envelope.ContainsKey(AgentEnvelope.Questions))
        {
            // Told why, rather than that the schema says no such member: the agent is to write them down instead.
            problems.Add(_output.AskRounds is null ? NobodyAnswers : AskedAsOftenAsItMay);
            members = (JsonObject)envelope.DeepClone();
            members.Remove(AgentEnvelope.Questions);
        }

        problems.AddRange(JsonSchemaSubset.Problems(_members, members));

        if (envelope[AgentEnvelope.Notes] is JsonValue notes && notes.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace(notes.GetValue<string>()))
        {
            problems.Add(NotesEmpty);
        }

        var questions = offered ? envelope[AgentEnvelope.Questions] as JsonArray : null;
        var asks = questions is { Count: > 0 };

        // Each assumption and question is held to its record's own rules - those no schema says - once it has the record's
        // shape: one that has not has its problems from the envelope's check already.
        problems.AddRange(RecordProblems(envelope[AgentEnvelope.Assumptions] as JsonArray, AgentEnvelope.Assumptions, AssumptionShape, (AgentAssumption assumption) => assumption.Problems()));
        problems.AddRange(RecordProblems(questions, AgentEnvelope.Questions, QuestionShape, (AgentQuestion question) => question.Problems()));

        JsonNode? output = null;

        if (envelope.TryGetPropertyValue(AgentEnvelope.Output, out var given))
        {
            if (given is null)
            {
                if (!asks)
                {
                    problems.Add(offered
                        ? "$.output: is null, and the answer asks nothing; answer with the output, as its schema says, or ask the person under questions"
                        : "$.output: is null; answer with the output, as its schema says");
                }
            }
            else if (asks)
            {
                problems.Add(AskOrAnswer);
            }
            else
            {
                output = JsonSchemaSubset.Respelled(_step, given);
                problems.AddRange(JsonSchemaSubset.Problems(_step, output, $"$.{AgentEnvelope.Output}"));
            }
        }

        if (problems.Count > 0)
        {
            return (problems, null);
        }

        var said = _mask.Redact(envelope[AgentEnvelope.Notes]!.GetValue<string>());
        IReadOnlyList<AgentAssumption> assumed = [.. envelope[AgentEnvelope.Assumptions]!.AsArray().Select(item => Record<AgentAssumption>(item).MaskedBy(_mask))];

        return ([], asks
            ? new AgentRunResult(AgentRunStatus.Asked, FinalText: _mask.Redact(text), Notes: said)
            {
                Assumptions = assumed,
                Questions = [.. questions!.Select(item => Record<AgentQuestion>(item).MaskedBy(_mask))],
            }
            : new AgentRunResult(AgentRunStatus.Completed, FinalText: _mask.Redact(text), StructuredOutput: Masked(output!), Notes: said)
            {
                Assumptions = assumed,
            });
    }

    /// <summary>
    /// <paramref name="output"/> as JSON, masked: each string where it stands before it is written - for JSON escapes a
    /// secret written in it, past a mask that reads the written text - then the written text, for the names of its members.
    /// </summary>
    /// <param name="output">The output, the gate's own copy; masked in place.</param>
    private string Masked(JsonNode output) => _mask.Redact(StoredStream.Masked(output, _mask)!.ToJsonString());

    /// <summary>An item the envelope's check passed, read as the record it is: its members are the record's (<see cref="AgentEnvelope.Member"/>).</summary>
    private static T Record<T>(JsonNode? item) => item.Deserialize<T>(JsonStateFile.Options)!;

    /// <summary>
    /// What is wrong with each item of the envelope's <paramref name="list"/> that has its record's <paramref name="shape"/>,
    /// by the record's own rule, each problem named by its path in the answer.
    /// </summary>
    private static IEnumerable<string> RecordProblems<T>(JsonArray? items, string list, JsonObject shape, Func<T, IReadOnlyList<string>> problems)
        => (items ?? []).SelectMany((item, at) => JsonSchemaSubset.Problems(shape, item).Count > 0
            ? []
            : problems(Record<T>(item)).Select(problem => $"$.{list}[{at}].{problem}"));
}
