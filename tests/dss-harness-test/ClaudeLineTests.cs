using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;

namespace DssHarness.Tests;

/// <summary>
/// How the client reads a launch's output: every line of every curated recording, on every channel, read as the kind it
/// is with nothing lost, and each kind's members read as the probe recorded them.
/// </summary>
public sealed class ClaudeLineTests
{
    /// <summary>The line kinds the client passes by, as the recordings hold them.</summary>
    private static readonly HashSet<(string, string?)> PassedBy =
    [
        ("system", "status"),
        ("system", "thinking_tokens"),
        ("system", "task_summary"),
        ("system", "model_fallback"),
    ];

    public static TheoryData<string> Channels => ["oldest", "stable", "latest"];

    /// <summary>
    /// Every line of every recording reads, each as its kind: a launch's start, a message, a piece, a result and each
    /// control message by its own record, and nothing else passed by but the kinds the client has no use for.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void EveryRecordedLine_ReadsAsItsKind(string channel)
    {
        var files = Directory.GetFiles(ClaudeCodeProbe.Fixtures(channel), "*.jsonl").Where(file => Path.GetFileName(file) != "findings.jsonl").ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            foreach (var text in File.ReadLines(file).Where(line => line.Length > 0))
            {
                var line = ClaudeLine.Read(text);
                Assert.NotNull(line);

                var type = line.Node["type"]!.GetValue<string>();
                var subtype = line.Node["subtype"]?.GetValue<string>();

                switch (line)
                {
                    case ClaudeLine.Init init:
                        Assert.Equal(("system", "init"), (type, subtype));
                        Assert.False(string.IsNullOrEmpty(init.Model));
                        Assert.Contains("Read", init.Tools);
                        Assert.False(string.IsNullOrEmpty(init.ApiKeySource));
                        Assert.Matches(@"^\d+\.\d+\.\d+$", init.Version);
                        break;
                    case ClaudeLine.Assistant assistant:
                        Assert.Equal("assistant", type);
                        Assert.False(string.IsNullOrEmpty(assistant.Model), $"{file}: an assistant message names no model");
                        Assert.All(assistant.Calls, call => Assert.False(string.IsNullOrEmpty(call.Id) || string.IsNullOrEmpty(call.Tool)));
                        break;
                    case ClaudeLine.User user:
                        Assert.Equal("user", type);
                        Assert.All(user.Results, result => Assert.StartsWith("toolu_", result.CallId, StringComparison.Ordinal));
                        break;
                    case ClaudeLine.Piece piece:
                        Assert.Equal("stream_event", type);
                        Assert.False(string.IsNullOrEmpty(piece.Kind));
                        break;
                    case ClaudeLine.Result result:
                        Assert.Equal("result", type);
                        Assert.Equal(subtype, result.Subtype);
                        Assert.False(string.IsNullOrEmpty(result.TerminalReason), $"{file}: a result says no terminal reason");
                        Assert.NotNull(result.CostUsd);
                        break;
                    case ClaudeLine.ControlRequest request:
                        Assert.Equal("control_request", type);
                        Assert.Equal("can_use_tool", request.Subtype);
                        Assert.False(string.IsNullOrEmpty(request.RequestId) || string.IsNullOrEmpty(request.Tool));
                        break;
                    case ClaudeLine.ControlResponse response:
                        Assert.Equal("control_response", type);
                        Assert.False(string.IsNullOrEmpty(response.RequestId));
                        break;
                    case ClaudeLine.Refusal refusal:
                        Assert.Equal(("system", "permission_denied"), (type, subtype));
                        Assert.False(string.IsNullOrEmpty(refusal.Tool));
                        break;
                    case ClaudeLine.RateLimit limit:
                        Assert.Equal("rate_limit_event", type);
                        Assert.Equal("allowed", limit.Status);
                        Assert.NotNull(limit.ResetsAt);
                        Assert.False(string.IsNullOrEmpty(limit.Window));
                        break;
                    case ClaudeLine.Other other:
                        Assert.Contains((other.Type, other.Subtype), PassedBy);
                        break;
                    default:
                        Assert.Fail($"{file}: '{text}' read as {line.GetType().Name}, which no recording holds");
                        break;
                }
            }
        }
    }

    /// <summary>
    /// The scenario's calls pair up: each result a user message carries answers a call an assistant message made before
    /// it, and each ask the launch put names a call it made.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheScenariosCalls_PairWithTheirResultsAndAsks(string channel)
    {
        var lines = File.ReadLines(ClaudeCodeProbe.Fixtures(channel, "shapes.jsonl")).Select(ClaudeLine.Read).ToList();
        var made = new HashSet<string>(StringComparer.Ordinal);
        var answered = 0;
        var asked = 0;

        foreach (var line in lines)
        {
            switch (line)
            {
                case ClaudeLine.Assistant assistant:
                    made.UnionWith(assistant.Calls.Select(call => call.Id));
                    break;
                case ClaudeLine.User { Results.Count: > 0 } user:
                    Assert.All(user.Results, result => Assert.Contains(result.CallId, made));
                    answered += user.Results.Count;
                    break;
                case ClaudeLine.ControlRequest request:
                    Assert.Contains(request.CallId ?? string.Empty, made);
                    asked++;
                    break;
            }
        }

        Assert.True(answered > 0 && asked > 0, $"{channel}: {answered} results, {asked} asks");

        // Whether a call ran is read from what Claude Code stamps beside its result: every call the host allowed ran,
        // every one it refused or could not decide did not, on every version.
        var stamped = lines.OfType<ClaudeLine.User>()
            .SelectMany(user => user.Results.Select(result => (Result: result, Meta: (user.Node["tool_result_meta"] as JsonArray)?.SingleOrDefault(meta => meta?["id"]?.GetValue<string>() == result.CallId))))
            .ToList();
        Assert.All(stamped, pair => Assert.Equal(pair.Meta?["non_execution_kind"] is null && (pair.Result.Ok || pair.Meta?["permission_decision"] is not null || !pair.Result.Content.StartsWith(ClaudeLine.InputErrorMark, StringComparison.Ordinal)), pair.Result.Ran));
        Assert.Contains(stamped, pair => pair.Result.Ran);
        Assert.Contains(stamped, pair => !pair.Result.Ran);
        Assert.Contains(lines, line => line is ClaudeLine.User { Replay: true });
        Assert.Contains(lines, line => line is ClaudeLine.User { Results: [{ Ok: false }, ..] });
    }

    /// <summary>
    /// An assistant message reads whole: its text blocks joined, its thinking, each call with its input, the error Claude
    /// Code wrote it for, and the model - <see cref="ClaudeLine.Synthetic"/> where no model wrote it.
    /// </summary>
    [Fact]
    public void AnAssistantMessage_ReadsWhole()
    {
        var line = Assert.IsType<ClaudeLine.Assistant>(ClaudeLine.Read(
            """{"type":"assistant","message":{"model":"claude-sonnet-5-5","content":[{"type":"thinking","thinking":"hm"},{"type":"text","text":"a"},{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"x"}},{"type":"text","text":"b"}],"usage":{"input_tokens":3,"output_tokens":4}}}"""));

        Assert.Equal(("claude-sonnet-5-5", "ab", "hm", (string?)null), (line.Model, line.Text, line.Thinking, line.Error));
        var call = Assert.Single(line.Calls);
        Assert.Equal(("toolu_1", "Read", "x"), (call.Id, call.Tool, call.Input!["file_path"]!.GetValue<string>()));
        Assert.Equal(3, line.Usage!.InputTokens);

        var refused = Assert.IsType<ClaudeLine.Assistant>(ClaudeLine.Read(
            """{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"Invalid API key"}]},"error":"authentication_failed"}"""));

        Assert.Equal((ClaudeLine.Synthetic, "authentication_failed", (string?)null), (refused.Model, refused.Error, refused.Thinking));
        Assert.Null(refused.Usage);
        Assert.Equal(((string?)null, (string?)null), (refused.StopReason, refused.Category));

        var declined = Assert.IsType<ClaudeLine.Assistant>(ClaudeLine.Read(
            """{"type":"assistant","message":{"model":"claude-sonnet-5-5","content":[],"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber"}}}"""));

        Assert.Equal(("refusal", "cyber"), (declined.StopReason, declined.Category));
    }

    /// <summary>
    /// A user message reads as an echo or not, its text, and each call's result with whether it succeeded and whether its
    /// tool ran: one stamped with why it did not run did not, one that failed with Claude Code's input error failed on its
    /// input before anything was asked, and any other ran - a failure after its permission was decided, under that mark or
    /// not, and a failure a version that stamps nothing wrote, included.
    /// </summary>
    [Fact]
    public void AUserMessage_ReadsItsEchoAndItsResults()
    {
        var echo = Assert.IsType<ClaudeLine.User>(ClaudeLine.Read("""{"type":"user","message":{"content":"go"},"uuid":"u1","isReplay":true}"""));
        Assert.Equal((true, "go"), (echo.Replay, echo.Text));

        var results = Assert.IsType<ClaudeLine.User>(ClaudeLine.Read(
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"ok"},{"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":[{"type":"text","text":"no"},{"type":"text","text":"pe"}]},{"type":"tool_result","tool_use_id":"toolu_3","is_error":true,"content":"exit 1"},{"type":"tool_result","tool_use_id":"toolu_4","is_error":true,"content":"denied"},{"type":"tool_result","tool_use_id":"toolu_5","is_error":true,"content":"<tool_use_error>InputValidationError: no path</tool_use_error>"},{"type":"tool_result","tool_use_id":"toolu_6","is_error":true,"content":"<tool_use_error>File does not exist.</tool_use_error>"}]},"tool_result_meta":[{"id":"toolu_3","permission_decision":{"decision":"accept","source":"user_temporary"}},{"id":"toolu_4","non_execution_kind":"permission-rule","permission_decision":{"decision":"reject","source":"user_reject"}},{"id":"toolu_6","permission_decision":{"decision":"accept","source":"config"}}]}"""));

        Assert.False(results.Replay);
        Assert.Equal(
            [
                new ClaudeLine.CallResult("toolu_1", true, "ok", Ran: true),
                new ClaudeLine.CallResult("toolu_2", false, "nope", Ran: true),
                new ClaudeLine.CallResult("toolu_3", false, "exit 1", Ran: true),
                new ClaudeLine.CallResult("toolu_4", false, "denied", Ran: false),
                new ClaudeLine.CallResult("toolu_5", false, "<tool_use_error>InputValidationError: no path</tool_use_error>", Ran: false),
                new ClaudeLine.CallResult("toolu_6", false, "<tool_use_error>File does not exist.</tool_use_error>", Ran: true),
            ],
            results.Results);
    }

    /// <summary>A result reads its subtype, its answer as text and as held to the schema, its errors, its cost and its use.</summary>
    [Fact]
    public void AResult_ReadsItsAnswerItsErrorsAndItsCost()
    {
        var result = Assert.IsType<ClaudeLine.Result>(ClaudeLine.Read(
            """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"t","structured_output":{"a":1},"errors":["e1","e2"],"terminal_reason":"aborted_streaming","stop_reason":"end_turn","total_cost_usd":0.125,"num_turns":2,"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":7,"cache_creation_input_tokens":9,"cache_creation":{"ephemeral_1h_input_tokens":4},"server_tool_use":{"web_search_requests":1}}}"""));

        Assert.Equal(("error_during_execution", true, "t"), (result.Subtype, result.IsError, result.Text));
        Assert.Equal("""{"a":1}""", result.Structured!.ToJsonString());
        Assert.Equal(["e1", "e2"], result.Errors);
        Assert.Equal(("aborted_streaming", "end_turn", 0.125m, 2), (result.TerminalReason, result.StopReason, result.CostUsd, result.Turns));
        Assert.Equal(new AgentUsage(10, 5, 7, 5, 4, 1), result.Usage);
    }

    /// <summary>The pieces a front end shows read by what they add; any other piece by its own type.</summary>
    [Fact]
    public void APiece_ReadsByWhatItAdds()
    {
        Assert.Equal(("text", 0, "hi"), Piece("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}"""));
        Assert.Equal(("thinking", 1, "hm"), Piece("""{"type":"content_block_delta","index":1,"delta":{"type":"thinking_delta","thinking":"hm"}}"""));
        Assert.Equal(("input", 2, "{\"a"), Piece("""{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"a"}}"""));
        Assert.Equal(("call", 2, "Read"), Piece("""{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_9","name":"Read"}}"""));
        Assert.Equal(("start", (int?)null, "claude-sonnet-5-5"), Piece("""{"type":"message_start","message":{"model":"claude-sonnet-5-5"}}"""));
        Assert.Equal(("message_stop", (int?)null, (string?)null), Piece("""{"type":"message_stop"}"""));
        Assert.Equal(("stop", (int?)null, "end_turn"), Piece("""{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_details":null}}"""));
        Assert.Equal(("stop", (int?)null, "refusal:cyber"), Piece("""{"type":"message_delta","delta":{"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber"}}}"""));
        Assert.Equal(("message_delta", (int?)null, (string?)null), Piece("""{"type":"message_delta","delta":{}}"""));

        static (string, int?, string?) Piece(string @event)
        {
            var piece = Assert.IsType<ClaudeLine.Piece>(ClaudeLine.Read($$"""{"type":"stream_event","event":{{@event}}}"""));
            return (piece.Kind, piece.Index, piece.Text);
        }
    }

    /// <summary>The control messages read by their ids: an ask with its call, an answer with whether it succeeded, a withdrawal.</summary>
    [Fact]
    public void TheControlMessages_ReadByTheirIds()
    {
        var ask = Assert.IsType<ClaudeLine.ControlRequest>(ClaudeLine.Read(
            """{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"},"tool_use_id":"toolu_3"}}"""));
        Assert.Equal(("r1", "can_use_tool", "Bash", "toolu_3"), (ask.RequestId, ask.Subtype, ask.Tool, ask.CallId));
        Assert.Equal("ls", ask.Input!["command"]!.GetValue<string>());

        var failed = Assert.IsType<ClaudeLine.ControlResponse>(ClaudeLine.Read("""{"type":"control_response","response":{"subtype":"error","request_id":"r2","error":"no"}}"""));
        Assert.Equal(("r2", false, "no"), (failed.RequestId, failed.Succeeded, failed.Error));

        var receipt = Assert.IsType<ClaudeLine.ControlResponse>(ClaudeLine.Read(
            """{"type":"control_response","response":{"subtype":"success","request_id":"interrupt","response":{"still_queued":[],"cancelled":["u2","u3"]}}}"""));
        Assert.Equal(["u2", "u3"], receipt.Cancelled);

        Assert.Equal("r3", Assert.IsType<ClaudeLine.ControlCancel>(ClaudeLine.Read("""{"type":"control_cancel_request","request_id":"r3"}""")).RequestId);
    }

    /// <summary>A retry reads its try, its bound, its wait and its cause; a refusal its tool and what it said.</summary>
    [Fact]
    public void ARetryAndARefusal_ReadWhatTheySay()
    {
        var retry = Assert.IsType<ClaudeLine.Retry>(ClaudeLine.Read("""{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"retry_delay_ms":1500,"error_status":529}"""));
        Assert.Equal((2, 10, TimeSpan.FromMilliseconds(1500), "529"), (retry.Attempt, retry.Of, retry.Delay, retry.Cause));

        var refusal = Assert.IsType<ClaudeLine.Refusal>(ClaudeLine.Read("""{"type":"system","subtype":"permission_denied","tool_name":"Write","tool_use_id":"toolu_4","message":"denied"}"""));
        Assert.Equal(("Write", "toolu_4", "denied"), (refusal.Tool, refusal.CallId, refusal.Message));
    }

    /// <summary>
    /// A rate limit event reads where the account stands, when the allowance resets, which allowance it is and why it was
    /// rejected; members it does not hold read as nothing.
    /// </summary>
    [Fact]
    public void ARateLimitEvent_ReadsWhereTheAccountStands()
    {
        var rejected = Assert.IsType<ClaudeLine.RateLimit>(ClaudeLine.Read(
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792422600,"rateLimitType":"seven_day","errorCode":"credits_required"}}"""));
        Assert.Equal(("rejected", (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(1792422600), "seven_day", "credits_required"), (rejected.Status, rejected.ResetsAt, rejected.Window, rejected.ErrorCode));

        var bare = Assert.IsType<ClaudeLine.RateLimit>(ClaudeLine.Read("""{"type":"rate_limit_event"}"""));
        Assert.Equal((string.Empty, (DateTimeOffset?)null, (string?)null, (string?)null), (bare.Status, bare.ResetsAt, bare.Window, bare.ErrorCode));
    }

    /// <summary>A line that is no JSON object reads as nothing, and the client passes it by.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void ALineThatIsNoJsonObject_ReadsAsNothing(string line) => Assert.Null(ClaudeLine.Read(line));

    /// <summary>What a turn used reads with the hour's cache writes apart from the rest, and nothing where it says nothing.</summary>
    [Fact]
    public void Usage_KeepsTheHoursCacheWritesApart()
    {
        Assert.Equal(AgentUsage.None, ClaudeLine.UsageOf(null));
        Assert.Equal(AgentUsage.None, ClaudeLine.UsageOf(new JsonArray()));
        Assert.Equal(new AgentUsage(1, 2, 0, 3, 0, 0), ClaudeLine.UsageOf(JsonNode.Parse("""{"input_tokens":1,"output_tokens":"2","cache_creation_input_tokens":3.0}""")));
    }
}
