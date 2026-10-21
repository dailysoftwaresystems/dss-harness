using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DssHarness.Core.Agents;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The stored stream: a step's transcript and there is no other - every event but the deltas, masked, one line each,
/// appended and on the disk per line, ending at its last whole event whatever killed its writer, and every line read
/// back as the event it was.
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
        var stream = StoredStream.Open(Disk(), Step(temp), new Masking("s3cret", "***"));

        await stream.EmitAsync(new SessionStarted("fake", "model-x", "hash", ["Read"], ["skill-a"], HandedConversation: false, SessionId: "s1"));
        await stream.EmitAsync(new MessageSent("the key is s3cret", AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new AssistantMessage("done, holding s3cret", [new AgentToolCall("c1", "Read")]));
        await stream.EmitAsync(new RunFinished(AgentRunStatus.Completed));

        stream.Dispose();

        var lines = File.ReadAllLines(StreamFile(temp));

        Assert.Equal(["sessionStarted", "messageSent", "assistantMessage", "runFinished"], lines.Select(Kind));
        Assert.DoesNotContain("s3cret", File.ReadAllText(StreamFile(temp)), StringComparison.Ordinal);
        Assert.Equal("the key is ***", (string)JsonNode.Parse(lines[1])!["text"]!);
        Assert.Equal("done, holding ***", (string)JsonNode.Parse(lines[2])!["text"]!);
    }

    /// <summary>
    /// No delta is stored, and nothing a delta held is missing from the message that follows it: the whole message is
    /// the record, and the file stays readable.
    /// </summary>
    [Fact]
    public async Task NoDeltaIsStored_AndNothingADeltaHeldIsMissingFromTheMessageThatFollows()
    {
        using var temp = new TempDirectory();
        var stream = StoredStream.Open(Disk(), Step(temp), new Masking("s3cret", "***"));

        await stream.EmitAsync(new TextDelta("the ans"));
        await stream.EmitAsync(new TextDelta("wer is 42"));
        await stream.EmitAsync(new ThinkingDelta("thinking aloud"));
        await stream.EmitAsync(new ToolCallInputDelta("c1", "{\"path\":"));
        await stream.EmitAsync(new AssistantMessage("the answer is 42", [new AgentToolCall("c1", "Read")], "thinking aloud"));
        await stream.EmitAsync(new ToolCallFinished("c1", Ok: true, TimeSpan.FromSeconds(2), "{\"path\": \"a.txt\"}", "the file's text", ResultFile: null));
        await stream.EmitAsync(new RunFinished(AgentRunStatus.Completed));

        stream.Dispose();

        var lines = File.ReadAllLines(StreamFile(temp));

        Assert.Equal(["assistantMessage", "toolCallFinished", "runFinished"], lines.Select(Kind));
        Assert.Equal("the answer is 42", (string)JsonNode.Parse(lines[0])!["text"]!);
        Assert.Equal("thinking aloud", (string)JsonNode.Parse(lines[0])!["thinking"]!);
        Assert.Equal("the file's text", (string)JsonNode.Parse(lines[1])!["result"]!);
    }

    /// <summary>
    /// A secret is masked wherever an event holds it: a string held directly in a list (an output repair's problems, the
    /// tools a launch was given), an object inside an object (the credential a launch names), and an object inside a list
    /// (a question the agent asks) - each to its exact masked value, and nothing else changed.
    /// </summary>
    [Fact]
    public async Task ASecretIsMasked_WhereverAnEventHoldsIt()
    {
        using var temp = new TempDirectory();
        var stream = StoredStream.Open(Disk(), Step(temp), new Masking("s3cret", "***"));

        await stream.EmitAsync(new OutputRepair(2, ["'answer' is s3cret, not a number", "'notes' is missing"]));
        await stream.EmitAsync(new SessionStarted(
            "fake",
            "model-x",
            "hash",
            ["Read", "s3cret-tool"],
            [],
            HandedConversation: false,
            Credential: new AgentCredentialSource(AgentCredentialSource.EnvironmentFile, "KEY_s3cret")));
        await stream.EmitAsync(new PersonAsked([new AgentQuestion("is s3cret the answer?", ["yes", "s3cret"], "no")]));

        stream.Dispose();

        var lines = File.ReadAllLines(StreamFile(temp)).Select(line => JsonNode.Parse(line)!).ToList();

        Assert.Equal(["'answer' is ***, not a number", "'notes' is missing"], lines[0]["problems"]!.AsArray().Select(item => (string)item!));
        Assert.Equal(["Read", "***-tool"], lines[1]["tools"]!.AsArray().Select(item => (string)item!));
        Assert.Equal("{\"kind\":\"EnvFile\",\"variable\":\"KEY_***\"}", lines[1]["credential"]!.ToJsonString());
        Assert.Equal(
            "[{\"text\":\"is *** the answer?\",\"choices\":[\"yes\",\"***\"],\"recommended\":\"no\"}]",
            lines[2]["questions"]!.ToJsonString());
    }

    /// <summary>
    /// The masking goes all the way down whatever holds what, a list directly inside a list included - a shape no event
    /// holds today and the next one may: every string is masked, and every other value is left as it was.
    /// </summary>
    [Fact]
    public void TheMasking_GoesAllTheWayDown_AListInsideAListIncluded()
    {
        var node = JsonNode.Parse("""{"a":[["s3cret one",["deeper s3cret",7]],{"b":{"c":"s3cret"}}],"d":true,"e":null}""");

        var masked = StoredStream.Masked(node, new Masking("s3cret", "***"));

        Assert.Equal("""{"a":[["*** one",["deeper ***",7]],{"b":{"c":"***"}}],"d":true,"e":null}""", masked!.ToJsonString());
    }

    /// <summary>
    /// A line is on the disk before the next event is taken: the file reads whole while its writer is still at work,
    /// which is what a kill leaves behind and what a stalled step's only evidence is.
    /// </summary>
    [Fact]
    public async Task ALineIsFlushedBeforeTheNextEventIsTaken_ReadsWholeWhileTheWriterWorks()
    {
        using var temp = new TempDirectory();
        var stream = StoredStream.Open(Disk(), Step(temp), new Masking("s3cret", "***"));

        await stream.EmitAsync(new MessageSent("one", AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new AssistantMessage("two", []));

        // A reader of a live stream stands beside its writer: it opens sharing what the writer holds.
        string[] whileOpen;

        using (var beside = new FileStream(StreamFile(temp), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reading = new StreamReader(beside))
        {
            whileOpen = reading.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        stream.Dispose();

        Assert.Equal(["one", "two"], whileOpen.Select(line => (string)JsonNode.Parse(line)!["text"]!));
    }

    /// <summary>
    /// A writer killed mid-line leaves the file ending at its last whole event, and one opened again begins there: the
    /// torn line is taken back, whatever it held.
    /// </summary>
    [Fact]
    public async Task ATornLineIsTakenBackOnOpen_AndTheStreamGoesOnFromTheLastWholeEvent()
    {
        using var temp = new TempDirectory();
        var first = StoredStream.Open(Disk(), Step(temp), new Masking("s3cret", "***"));

        await first.EmitAsync(new MessageSent("one", AgentMessageKind.FirstPrompt));
        await first.EmitAsync(new AssistantMessage("two", []));

        first.Dispose();

        // What a kill leaves: a line begun and never ended.
        File.AppendAllText(StreamFile(temp), "{\"kind\": \"assistantMess");

        var again = StoredStream.Open(Disk(), Step(temp), new Masking("s3cret", "***"));

        await again.EmitAsync(new RunFinished(AgentRunStatus.Completed));

        again.Dispose();

        Assert.Equal(["messageSent", "assistantMessage", "runFinished"], File.ReadAllLines(StreamFile(temp)).Select(Kind));
    }

    /// <summary>
    /// Opening a long record reads it backwards from its end, a chunk at a time, only as far as its last whole line: a
    /// torn tail near the end costs one chunk, not the whole file, and a torn tail longer than a chunk is still taken
    /// back whole to the line before it.
    /// </summary>
    [Fact]
    public void OpeningALongRecord_ReadsBackwardsInChunks_OnlyAsFarAsItsLastWholeLine()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("long.jsonl");
        var line = new string('x', 999) + "\n";
        File.WriteAllText(path, string.Concat(Enumerable.Repeat(line, 1_000)) + "{\"torn\"");

        var counting = new CountingReads(Disk());

        new OpenedLog(counting, path).Dispose();

        Assert.Equal(1_000_000L, new FileInfo(path).Length);
        Assert.Equal(AppendLog.ScanChunk, counting.BytesRead);

        // A torn tail three chunks long: read back past it chunk by chunk, to the end of the line before it.
        File.WriteAllText(path, line + new string('y', AppendLog.ScanChunk * 3));
        var again = new CountingReads(Disk());

        new OpenedLog(again, path).Dispose();

        Assert.Equal(line, File.ReadAllText(path));
        Assert.Equal(line.Length + (AppendLog.ScanChunk * 3), again.BytesRead);
    }

    /// <summary>
    /// A line that holds a line break of its own is refused before anything is written: cut after the inner break by a
    /// kill, it would be taken back as half a record that looks whole.
    /// </summary>
    [Fact]
    public void ALineWithALineBreakOfItsOwn_IsRefused_AndNothingIsWritten()
    {
        using var temp = new TempDirectory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var conversation = StoredConversation.Open(Disk(), layout, "a7", "conv1", "the system prompt", "{\"tools\": []}");
        var file = Path.Combine(conversation.SessionDirectory, StoredConversation.ConversationFileName);

        conversation.Append("{\"turn\": 1}");

        var broken = Assert.Throws<ArgumentException>(() => conversation.Append("{\"turn\":\n 2}"));
        var returned = Assert.Throws<ArgumentException>(() => conversation.Append("{\"turn\": 3}\r"));

        conversation.Dispose();

        Assert.Equal($"A line of '{file}' holds a line break at character 8, and would be read back as two; nothing was written. (Parameter 'line')", broken.Message);
        Assert.Equal($"A line of '{file}' holds a line break at character 11, and would be read back as two; nothing was written. (Parameter 'line')", returned.Message);
        Assert.Equal("{\"turn\": 1}\n", File.ReadAllText(file));
    }

    /// <summary>
    /// Every kind of event reads back as the event it was written as, with its members that may be absent left out and
    /// with them all set: a line is written without its null members and read back by options that refuse a member the
    /// type needs and the line leaves out, so a kind whose nullable member had no default would make its own lines
    /// unreadable. Every kind is built here from its own constructor, so a new kind is held to it without being named.
    /// </summary>
    [Fact]
    public async Task EveryKindOfEvent_ReadsBackAsItWasWritten_WithItsAbsentMembersAndWithThemSet()
    {
        var kinds = typeof(AgentEvent).GetCustomAttributes<JsonDerivedTypeAttribute>().ToList();

        // Every event there is is a kind the line's form names, and the kinds are the plan's table with the run's
        // beginning beside its end.
        Assert.Equal(
            kinds.Select(kind => kind.DerivedType).OrderBy(type => type.Name, StringComparer.Ordinal),
            typeof(AgentEvent).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(AgentEvent)) && !type.IsAbstract).OrderBy(type => type.Name, StringComparer.Ordinal));
        Assert.Equal(
            ["sessionStarted", "messageSent", "turnStarted", "turnFinished", "textDelta", "thinkingDelta", "assistantMessage",
                "toolCallStarted", "toolCallInputDelta", "toolCallFinished", "permissionDenied", "webRequested", "usageReported",
                "waitingToRetry", "turnRestarted", "fallbackStarted", "fallbackPassedOver", "outputRepair", "compacted",
                "handoffAsked", "handoffAnswered", "personAsked", "personAnswered", "notice", "runStarted", "runFinished"],
            kinds.Select(kind => (string)kind.TypeDiscriminator!));

        using var temp = new TempDirectory();
        var stream = StoredStream.Open(Disk(), Step(temp), new Masking("s3cret", "***"));
        var stored = new List<(AgentEvent Event, Fill Fill)>();
        var shown = new List<(AgentEvent Event, Fill Fill, string Line)>();

        foreach (var fill in Enum.GetValues<Fill>())
        {
            foreach (var kind in kinds)
            {
                var @event = (AgentEvent)Sample(kind.DerivedType, fill)!;

                // A delta is never stored, and is read in the same form by whoever is shown it.
                if (@event is TextDelta or ThinkingDelta or ToolCallInputDelta)
                {
                    shown.Add((@event, fill, JsonSerializer.Serialize(@event, JsonStateFile.LineOptions)));
                }
                else
                {
                    await stream.EmitAsync(@event);
                    stored.Add((@event, fill));
                }
            }
        }

        stream.Dispose();

        var lines = File.ReadAllLines(StreamFile(temp));

        Assert.Equal(stored.Count, lines.Length);
        Assert.Equal(26 * 3, stored.Count + shown.Count);

        foreach (var (@event, fill, line) in stored.Zip(lines, (one, line) => (one.Event, one.Fill, line)).Concat(shown))
        {
            var read = JsonSerializer.Deserialize<AgentEvent>(line, JsonStateFile.LineOptions);

            Assert.IsType(@event.GetType(), read);
            Assert.Equal(line, JsonSerializer.Serialize(read, JsonStateFile.LineOptions));
            AssertWhole(line, fill);
        }
    }

    /// <summary>
    /// The result reads back as it was written to <c>result.json</c>, by the options it is written with: with its members
    /// that may be absent left out, with the objects that may be absent there and their own members left out, and with
    /// everything set - each model's part of the usage and the cost's two numbers among them.
    /// </summary>
    [Fact]
    public void TheResult_ReadsBackAsItWasWritten_WithItsAbsentMembersAndWithThemSet()
    {
        foreach (var fill in Enum.GetValues<Fill>())
        {
            var result = (AgentRunResult)Sample(typeof(AgentRunResult), fill)!;
            var text = JsonSerializer.Serialize(result, JsonStateFile.Options);

            var read = JsonSerializer.Deserialize<AgentRunResult>(text, JsonStateFile.Options);

            Assert.Equal(text, JsonSerializer.Serialize(read, JsonStateFile.Options));
            AssertWhole(text, fill);
        }
    }

    /// <summary>
    /// A null item is refused in every list an event or the result holds, read back by the options each was written
    /// with - through a line of the stored stream and through <c>result.json</c> - as it is in a list of the
    /// configuration: a record declares its lists read-only, and a refusal that reached only the concrete list would
    /// read a null back where every reader trusts there is none. Each list is found by its member's own type, a list
    /// within a list's item among them, so one added later is held to it without being named.
    /// </summary>
    [Fact]
    public void ANullItem_IsRefusedInEveryListALineOrTheResultHolds()
    {
        var refused = new List<string>();

        foreach (var kind in typeof(AgentEvent).GetCustomAttributes<JsonDerivedTypeAttribute>())
        {
            var line = JsonSerializer.SerializeToNode((AgentEvent)Sample(kind.DerivedType, Fill.Everything)!, JsonStateFile.LineOptions)!;

            foreach (var (path, broken) in WithANullItem(line, kind.DerivedType, (string)kind.TypeDiscriminator!))
            {
                var read = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentEvent>(broken, JsonStateFile.LineOptions));

                Assert.Equal("Item 1 of a list is null; a list in this file cannot contain null.", read.Message);
                refused.Add(path);
            }
        }

        var result = JsonSerializer.SerializeToNode((AgentRunResult)Sample(typeof(AgentRunResult), Fill.Everything)!, JsonStateFile.Options)!;

        foreach (var (path, broken) in WithANullItem(result, typeof(AgentRunResult), AgentRunner.ResultFileName))
        {
            var read = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentRunResult>(broken, JsonStateFile.Options));

            Assert.Equal("Item 1 of a list is null; a list in this file cannot contain null.", read.Message);
            refused.Add(path);
        }

        // Every list there is was found: a walk that found none would pass having held nothing.
        Assert.Equal(
            [
                "sessionStarted.tools", "sessionStarted.skills", "assistantMessage.calls", "outputRepair.problems",
                "personAsked.questions", "personAsked.questions[0].choices", "result.json.usage.perModel", "result.json.assumptions",
                "result.json.questions", "result.json.questions[0].choices", "result.json.served", "result.json.files",
            ],
            refused);
    }

    /// <summary>
    /// Each model's part of a run's usage is written by its names and read back as it was: a pair the serializer cannot
    /// name is written as nothing at all, and the usage of every model that served would be lost.
    /// </summary>
    [Fact]
    public void EachModelsPartOfTheUsage_IsWrittenByName_AndReadsBack()
    {
        var usage = new AgentUsageReport(
            new AgentUsage(30, 12, 5, 2, 3, 1),
            [new AgentModelUsage("model-x", new AgentUsage(10, 4, 5, 2, 3, 1)), new AgentModelUsage("model-y", new AgentUsage(20, 8, 0, 0, 0, 0))]);

        var line = JsonSerializer.Serialize(usage, JsonStateFile.LineOptions);
        var read = JsonSerializer.Deserialize<AgentUsageReport>(line, JsonStateFile.LineOptions)!;

        Assert.Equal(
            """{"total":{"inputTokens":30,"outputTokens":12,"cacheReadTokens":5,"cacheWriteTokens":2,"cacheWrite1hTokens":3,"searches":1},"perModel":[{"model":"model-x","usage":{"inputTokens":10,"outputTokens":4,"cacheReadTokens":5,"cacheWriteTokens":2,"cacheWrite1hTokens":3,"searches":1}},{"model":"model-y","usage":{"inputTokens":20,"outputTokens":8,"cacheReadTokens":0,"cacheWriteTokens":0,"cacheWrite1hTokens":0,"searches":0}}]}""",
            line);
        Assert.Equal([new AgentModelUsage("model-x", new AgentUsage(10, 4, 5, 2, 3, 1)), new AgentModelUsage("model-y", new AgentUsage(20, 8, 0, 0, 0, 0))], read.PerModel);
    }

    /// <summary>
    /// The cost is written as the plan's <c>result.json</c> keeps it: the client's reported number and the counted one
    /// side by side, and a cost no price was there for written as the price that was missing - never as a zero. The
    /// run's cost is the reported one where there is one.
    /// </summary>
    [Fact]
    public void TheCost_IsWrittenReportedAndCountedSideBySide_AndAnUnknownOneNamesThePriceMissing()
    {
        var both = new AgentCost(Reported: 0.0183m, Counted: 0.0179m);
        var unknown = new AgentCost(Reported: 0.0183m, MissingPrice: "no price for 'glm-5.3' (agentProviders.zai.prices.perMillionTokens)");
        var counted = new AgentCost(Counted: 0.42m);

        Assert.Equal("""{"reported":0.0183,"counted":0.0179}""", JsonSerializer.Serialize(both, JsonStateFile.LineOptions));
        Assert.Equal(
            """{"reported":0.0183,"missingPrice":"no price for 'glm-5.3' (agentProviders.zai.prices.perMillionTokens)"}""",
            JsonSerializer.Serialize(unknown, JsonStateFile.LineOptions));
        Assert.Equal("""{"counted":0.42}""", JsonSerializer.Serialize(counted, JsonStateFile.LineOptions));

        Assert.Equal(0.0183m, both.Usd);
        Assert.Equal(0.42m, counted.Usd);
        Assert.Null(new AgentCost(MissingPrice: "no price for 'glm-5.3' (agentProviders.zai.prices.perMillionTokens)").Usd);
        Assert.Equal(both, JsonSerializer.Deserialize<AgentCost>("""{"reported":0.0183,"counted":0.0179}""", JsonStateFile.LineOptions));
    }

    /// <summary>
    /// A stored conversation hangs off the same seam: what it began with is written once, and whole - each file put in
    /// place in one step through the file system it was given - and its turns are appended whole, on the disk per line,
    /// and recovered to the last whole line after a kill.
    /// </summary>
    [Fact]
    public void AStoredConversation_HangsOffTheSameSeam()
    {
        using var temp = new TempDirectory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var recording = new RecordingWrites(Disk());

        var begun = StoredConversation.Open(recording, layout, "a7", "conv1", "the system prompt", "{\"tools\": []}");

        begun.Append("{\"role\": \"user\", \"text\": \"one\"}");
        begun.Append("{\"role\": \"assistant\", \"text\": \"two\"}");

        begun.Dispose();

        var directory = layout.AgentSessionDirectory("a7", "conv1");

        // Each written once, in one step: a file that is there is one written to its end.
        Assert.Equal([Path.Combine(directory, StoredConversation.SystemFileName), Path.Combine(directory, StoredConversation.ToolsFileName)], recording.Written);

        File.AppendAllText(Path.Combine(directory, StoredConversation.ConversationFileName), "{\"role\": \"assista");

        var again = StoredConversation.Open(recording, layout, "a7", "conv1", "a different prompt, not written", "{\"tools\": [\"not written\"]}");

        again.Append("{\"role\": \"user\", \"text\": \"three\"}");

        again.Dispose();

        Assert.Equal(2, recording.Written.Count);
        Assert.Equal(
            ["{\"role\": \"user\", \"text\": \"one\"}", "{\"role\": \"assistant\", \"text\": \"two\"}", "{\"role\": \"user\", \"text\": \"three\"}"],
            File.ReadAllLines(Path.Combine(directory, StoredConversation.ConversationFileName)));
        Assert.Equal("the system prompt", File.ReadAllText(Path.Combine(directory, StoredConversation.SystemFileName)));
        Assert.Equal("{\"tools\": []}", File.ReadAllText(Path.Combine(directory, StoredConversation.ToolsFileName)));
    }

    /// <summary>How much of a sample is filled.</summary>
    private enum Fill
    {
        /// <summary>Every member set, those that may be absent among them.</summary>
        Everything,

        /// <summary>Every member that may be absent left out, save an object, which is there with its own left out.</summary>
        AbsentLeaves,

        /// <summary>Every member that may be absent left out, an object as much as a value.</summary>
        AbsentAll,
    }

    /// <summary>
    /// Where everything was set, the text holds no empty object and no empty list: a member written as nothing is one
    /// whose shape the serializer could not name, and lost what it held.
    /// </summary>
    private static void AssertWhole(string text, Fill fill)
    {
        if (fill == Fill.Everything)
        {
            Assert.DoesNotContain("{}", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[]", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A value of <paramref name="type"/>, built from its widest constructor and its settable members, every member that
    /// may be absent filled or left null as <paramref name="fill"/> says.
    /// </summary>
    private static object? Sample(Type type, Fill fill)
    {
        if (type == typeof(string))
        {
            return "text";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return fill == Fill.Everything ? Sample(underlying, fill) : null;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).Cast<object>().Last();
        }

        if (type == typeof(int))
        {
            return 3;
        }

        if (type == typeof(long))
        {
            return 4L;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(decimal))
        {
            return 0.0183m;
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.FromSeconds(2.5);
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 10, 18, 14, 40, 45, TimeSpan.Zero);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var item = type.GetGenericArguments()[0];
            var list = Array.CreateInstance(item, 1);
            list.SetValue(Sample(item, fill), 0);
            return list;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
        {
            throw new InvalidOperationException($"No sample is made for '{type}': give the builder one.");
        }

        var nullability = new NullabilityInfoContext();
        var constructor = type.GetConstructors().OrderByDescending(one => one.GetParameters().Length).First();
        var arguments = constructor.GetParameters()
            .Select(parameter => Absent(nullability.Create(parameter).WriteState, parameter.ParameterType, fill) ? null : Sample(parameter.ParameterType, fill))
            .ToArray();
        var made = constructor.Invoke(arguments);

        foreach (var property in type.GetProperties().Where(property => property.SetMethod is { IsPublic: true } && !constructor.GetParameters().Any(parameter => string.Equals(parameter.Name, property.Name, StringComparison.Ordinal))))
        {
            property.SetValue(made, Absent(nullability.Create(property).WriteState, property.PropertyType, fill) ? null : Sample(property.PropertyType, fill));
        }

        return made;
    }

    /// <summary>
    /// The document <paramref name="node"/> belongs to, written as a <paramref name="type"/> at <paramref name="path"/>,
    /// once for each list of items that are no value of their own that the type's members hold there: where the list
    /// is, and the whole document with a null item put at the list's end. A list's items, and an object a member holds,
    /// are walked by their own types.
    /// </summary>
    private static IEnumerable<(string Path, string Broken)> WithANullItem(JsonNode node, Type type, string path)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var at = $"{path}.{name}";

            if (node[name] is JsonArray list && ItemOf(property.PropertyType) is { IsValueType: false } item)
            {
                list.Add(null);
                yield return (at, node.Root.ToJsonString());
                list.RemoveAt(list.Count - 1);

                foreach (var (index, one) in list.Index())
                {
                    foreach (var within in one is JsonObject ? WithANullItem(one, item, $"{at}[{index}]") : [])
                    {
                        yield return within;
                    }
                }
            }
            else if (node[name] is JsonObject inner)
            {
                foreach (var within in WithANullItem(inner, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, at))
                {
                    yield return within;
                }
            }
        }
    }

    /// <summary>The item type of a sequence other than text, or <see langword="null"/> where the type is none.</summary>
    private static Type? ItemOf(Type type)
        => type == typeof(string)
            ? null
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(one => one.IsGenericType && one.GetGenericTypeDefinition() == typeof(IEnumerable<>))?
                .GetGenericArguments()[0];

    /// <summary>Whether a member that may be absent is left null, as <paramref name="fill"/> says of its kind.</summary>
    private static bool Absent(NullabilityState state, Type type, Fill fill)
        => fill switch
        {
            Fill.Everything => false,
            Fill.AbsentAll => state == NullabilityState.Nullable,
            _ => state == NullabilityState.Nullable && (type == typeof(string) || type.IsValueType),
        };

    /// <summary>The real file system.</summary>
    private static PhysicalFileSystem Disk() => new(FilePermissionsFactory.Create());

    /// <summary>The step's own directory, as the one owner of the paths spells it.</summary>
    private static string Step(TempDirectory temp)
        => Path.Combine(temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1");

    /// <summary>The step's stored stream.</summary>
    private static string StreamFile(TempDirectory temp) => Path.Combine(Step(temp), StoredStream.FileName);

    /// <summary>One line's event kind, as its own form names it.</summary>
    private static string Kind(string line) => (string)JsonNode.Parse(line)!["kind"]!;

    /// <summary>One append log opened at a path, as a record's writer opens it at its start.</summary>
    private sealed class OpenedLog(IFileSystem fileSystem, string path) : IDisposable
    {
        private readonly AppendLog _log = AppendLog.Open(fileSystem, path);

        public void Dispose() => _log.Dispose();
    }

    /// <summary>A mask that masks one secret, as a run that carries one is given.</summary>
    private sealed class Masking(string secret, string masked) : ILineMask
    {
        public string Redact(string? text) => (text ?? string.Empty).Replace(secret, masked, StringComparison.Ordinal);

        public int Cut(string text) => text.Length;
    }

    /// <summary>The real file system, counting the bytes read from a file opened for appending.</summary>
    private sealed class CountingReads(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public long BytesRead { get; private set; }

        public override IAppendFile OpenForAppend(string path) => new Counted(base.OpenForAppend(path), this);

        private sealed class Counted(IAppendFile file, CountingReads reads) : IAppendFile
        {
            public long Length => file.Length;

            public void ReadExactlyAt(long position, Span<byte> buffer)
            {
                file.ReadExactlyAt(position, buffer);
                reads.BytesRead += buffer.Length;
            }

            public void TruncateTo(long length) => file.TruncateTo(length);

            public void AppendFlushed(ReadOnlySpan<byte> bytes) => file.AppendFlushed(bytes);

            public void Dispose() => file.Dispose();
        }
    }

    /// <summary>The real file system, recording each file written in one step.</summary>
    private sealed class RecordingWrites(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public List<string> Written { get; } = [];

        public override void WriteAllTextAtomic(string path, string contents)
        {
            Written.Add(path);
            base.WriteAllTextAtomic(path, contents);
        }
    }
}
