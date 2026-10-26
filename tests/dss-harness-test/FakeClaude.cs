using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DssHarness.Tests;

/// <summary>
/// The fake <c>claude</c> played from a script: the test child's <c>claude-code</c> mode where <see cref="ScriptVariable"/>
/// names a script, speaking the stream both ways in the shapes the probe recorded. Each user message it takes begins a
/// turn, played from the script's entry of that turn: the calls it asks the host about and waits on, the model each
/// message names, an error Claude Code writes itself, how the turn ends, and the answer. A turn that hangs waits for the
/// host's interrupt - and, told to be slow, ends the turn it cut that many milliseconds after it took the interrupt; one
/// that exits ends the process as the turn begins, by the code it gives, writing nothing more.
/// Everything it was started with and everything it read is written to <see cref="RecordVariable"/>'s
/// file, for a test to hold the client to.
/// </summary>
/// <remarks>
/// It can also be a launch the engine is not the host of, or one that breaks the protocol, as the client's checks are
/// held to: the first turn's <c>noInitialize</c> leaves the <c>initialize</c> request unanswered and plays nothing, its
/// <c>initializeError</c> refuses it, and its <c>initializeAfterMs</c> answers it that long after reading it; a turn's
/// <c>initTools</c> and <c>apiKeySource</c> are what its init says, and its <c>noInit</c> writes none, as a launch that
/// works before it says what it runs as; a turn's <c>refuseInterrupt</c> answers the interrupt with an error and goes on,
/// its <c>stderr</c> is a line it writes on its standard error as it begins, and its <c>exitAfter</c> ends the process by
/// that code once its answer's text is written, before the turn's result; a message the next turn's <c>untaken</c> names is
/// never taken, one its <c>takenLate</c> names was taken for its turn as the interrupt came - the receipt names it in
/// neither list, and its turn starts once the receipt is written, cut at once, as Claude Code 2.1.296 was seen to do -
/// and one its <c>takeAfterMs</c> names is taken that long after it is read. The interrupt's receipt names each waiting
/// message it dropped by the id the host gave it - and, where a turn's <c>receiptAlsoNames</c> says, ids of messages no
/// host wrote - and each echo carries that id; a turn's <c>waitForMessage</c> waits once its calls are done for a
/// message written while it works, and takes it into the same turn, as 2.1.296 was seen to take one written while a
/// call was asked. A call can run <c>unasked</c> (or fail after running unasked, <c>unaskedFailed</c>, with Claude
/// Code's input-error mark, <c>unaskedFailedMarked</c>, or <c>unaskedFailedBare</c> as a version that stamps nothing
/// beside a result), fail on its input before anything is asked (<c>invalidInput</c>), be refused by Claude Code itself
/// (<c>refusedByClaude</c>), by its own deny rule as a file tool's call is - told by its result alone, stamped as 2.1.308
/// stamps it (<c>deniedByRule</c>) or bare as 2.1.287 writes it (<c>deniedByRuleBare</c>) - or told by a
/// <c>permission_denied</c> line and that result both (<c>deniedByRuleTold</c>), run though the host denied it (<c>ranThoughDenied</c>), or be cut by the interrupt while
/// it is asked, whatever the host answered (<c>cutWhileAsked</c>: the ask withdrawn, the call stamped
/// <c>user-rejected</c>, as 2.1.296 was seen to do). A turn can be <c>declined</c> (the category, or empty for none;
/// said where <c>declinedBy</c> names, or everywhere), and can carry a <c>rateLimit</c> event's info first, a
/// <c>controlRequest</c> of its own that it does not wait on, and take <c>slowMs</c> before it plays. Each answer of
/// the host's to a call is written back, as the CLI writes it. Its <c>ignoreEof</c> keeps the process running once its
/// input is closed, as a launch that does not end by itself.
/// <para>
/// Where <c>--mcp-config</c> names a server of type <c>sdk</c>, it connects it before its first init as the probe saw
/// Claude Code do - <c>initialize</c>, <c>notifications/initialized</c>, <c>tools/list</c>, each an <c>mcp_message</c> it
/// waits on - and its init lists the server, <c>connected</c>, and its tools as <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>. A
/// call of one is asked of the host, and once allowed sent to the server as <c>tools/call</c> with the call's id in its
/// <c>_meta</c>, its result the server's text. The first turn's <c>mcpStatus</c> connects nothing and says that status; a
/// turn's <c>initMcpServers</c> is what its init says of them; a call's <c>calledUnasked</c> sends <c>tools/call</c> with no
/// ask, its <c>calledThoughDenied</c> sends it though the host denied it, its <c>otherCallId</c> names another call in it -
/// one never made - its <c>otherToolsCallId</c> the turn's call before it, of another tool, its <c>noCallId</c> none, its
/// <c>calledTwice</c> sends it twice, its <c>otherServer</c> names a server it was not given, and its <c>cancelCall</c>
/// cancels it as it is sent.
/// </para>
/// </remarks>
internal static class FakeClaude
{
    /// <summary>
    /// The variable naming the script: a JSON array, one object for each turn - or an object whose <c>byKey</c> holds such
    /// an array for each key a launch may be given, by the key's value.
    /// </summary>
    internal const string ScriptVariable = "FAKE_CLAUDE_TURNS";

    /// <summary>The variable naming the file the fake records itself in: its start, then every line it read.</summary>
    internal const string RecordVariable = "FAKE_CLAUDE_RECORD";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Plays the script, as a launch of Claude Code would play its turns.</summary>
    /// <param name="output">Where the stream is written.</param>
    /// <param name="arguments">The argument list it was started with.</param>
    /// <param name="script">The script's file.</param>
    internal static int Run(TextWriter output, string[] arguments, string script)
    {
        // A script is the turns of every launch, or - under "byKey" - the turns of the launch given each key.
        var root = JsonNode.Parse(File.ReadAllText(script));
        var turns = root as JsonArray ?? root?["byKey"]?[Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? string.Empty] as JsonArray ?? [];
        var record = Environment.GetEnvironmentVariable(RecordVariable);
        var model = Option(arguments, "--model") ?? "claude-sonnet-5-5";
        var session = Option(arguments, "--session-id") ?? "00000000-0000-4000-8000-000000000001";
        var schema = Option(arguments, "--json-schema") is not null;
        var tools = (Option(arguments, "--tools") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        var first = turns.Count > 0 ? turns[0] as JsonObject : null;
        var ids = 0;

        // The servers --mcp-config names of type sdk, connected before the first init; what each request of theirs awaits.
        var servers = McpServers(Option(arguments, "--mcp-config"));
        var serverTools = new List<string>();
        var connected = servers.Count == 0 || first?["mcpStatus"] is not null;
        var awaited = new Dictionary<string, string>(StringComparer.Ordinal);
        var rpc = 1;

        if (record is not null)
        {
            var environment = new JsonObject();

            foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.Ordinal))
            {
                environment[name] = Environment.GetEnvironmentVariable(name);
            }

            File.AppendAllText(record, new JsonObject
            {
                ["argv"] = new JsonArray([.. arguments.Select(argument => (JsonNode)argument)]),
                ["cwd"] = Environment.CurrentDirectory,
                ["env"] = environment,
            }.ToJsonString() + "\n", Utf8NoBom);
        }

        var queued = new Queue<(string Content, string? Id)>();
        var held = new List<string?>();
        (string Content, string? Id)? late = null;
        var cutNext = false;
        var awaiting = false;
        var tookIn = false;
        string? cut = null;
        string? cutAsk = null;
        var turn = -1;
        var busy = false;
        JsonObject? playing = null;
        var callAt = 0;
        string? waitingOn = null;
        var calls = new List<JsonObject>();
        int? exit = null;

        void Say(JsonNode line)
        {
            output.Write(line.ToJsonString() + "\n");
            output.Flush();
        }

        string Id(string prefix) => $"{prefix}{(++ids).ToString("D24", CultureInfo.InvariantCulture)}";

        string Model(JsonObject entry) => Text(entry["model"]) ?? model;

        void Result(JsonObject entry, string? text, JsonNode? structured, bool error, string subtype, string terminal, IReadOnlyList<string> errors)
        {
            busy = false;
            var result = new JsonObject
            {
                ["type"] = "result",
                ["subtype"] = subtype,
                ["is_error"] = error,
                ["duration_ms"] = 10,
                ["num_turns"] = 1,
                ["session_id"] = session,
                ["total_cost_usd"] = (turn + 1) * 0.01,
                ["usage"] = new JsonObject { ["input_tokens"] = 6, ["output_tokens"] = 40, ["cache_read_input_tokens"] = 100, ["cache_creation_input_tokens"] = 0 },
                ["permission_denials"] = new JsonArray(),
                ["terminal_reason"] = terminal,
                ["stop_reason"] = entry.ContainsKey("declined") && Text(entry["declinedBy"]) is null or "result" && subtype == "success" ? "refusal" : "end_turn",
            };

            if (text is not null)
            {
                result["result"] = text;
            }

            if (structured is not null)
            {
                result["structured_output"] = structured.DeepClone();
            }

            if (errors.Count > 0)
            {
                result["errors"] = new JsonArray([.. errors.Select(line => (JsonNode)line)]);
            }

            Say(result);
            Next();
        }

        void Assistant(JsonObject entry, JsonArray content, string? error = null)
        {
            var line = new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject
                {
                    ["id"] = Id("msg_"),
                    ["model"] = error is null ? Model(entry) : "<synthetic>",
                    ["role"] = "assistant",
                    ["content"] = content,
                    ["usage"] = new JsonObject { ["input_tokens"] = 6, ["output_tokens"] = 20 },
                },
                ["session_id"] = session,
            };

            if (error is not null)
            {
                line["error"] = error;
            }

            Say(line);
        }

        // An MCP message of the server's, sent to the host and waited on as what it is.
        string Mcp(string server, JsonObject message, string answer)
        {
            var request = Id("mcp-");
            awaited[request] = answer;
            Say(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = request,
                ["request"] = new JsonObject { ["subtype"] = "mcp_message", ["server_name"] = server, ["message"] = message },
            });

            return request;
        }

        // A call of the server's tool sent to it, its id in its _meta as Claude Code names it there.
        void CallServer(JsonObject call, string tool)
        {
            var server = tool.Split("__")[1];
            var parameters = new JsonObject { ["name"] = tool[$"mcp__{server}__".Length..], ["arguments"] = call["input"]?.DeepClone() ?? new JsonObject() };

            if (!Flag(call, "noCallId"))
            {
                var named = Flag(call, "otherCallId") ? Id("toolu_") : Flag(call, "otherToolsCallId") ? Text(calls[callAt - 1]["id"]) : Text(call["id"]);
                parameters["_meta"] = new JsonObject { ["claudecode/toolUseId"] = named, ["progressToken"] = 2 };
            }

            var id = ++rpc;
            Mcp(Flag(call, "otherServer") ? "other" : server, new JsonObject { ["method"] = "tools/call", ["params"] = parameters, ["jsonrpc"] = "2.0", ["id"] = id }, "call");

            if (Flag(call, "calledTwice"))
            {
                Mcp(server, new JsonObject { ["method"] = "tools/call", ["params"] = parameters.DeepClone(), ["jsonrpc"] = "2.0", ["id"] = ++rpc }, "again");
            }

            if (Flag(call, "cancelCall"))
            {
                Mcp(server, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = "the fake cancelled it" } }, "ack");
            }
        }

        // The echo of a message taken, by the id the host gave it.
        void Echo(string content, string? id)
            => Say(new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
                ["session_id"] = session,
                ["uuid"] = id ?? Id("uuid-"),
                ["isReplay"] = true,
            });

        // The turn's next call, asked and waited on; its answer once every call was decided.
        void Play()
        {
            var entry = playing!;

            if (callAt < calls.Count)
            {
                var call = calls[callAt];
                var id = Id("toolu_");
                var tool = Text(call["tool"]) ?? "Read";
                var input = call["input"]?.DeepClone() ?? new JsonObject();
                call["id"] = id;

                Say(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["model"] = Model(entry) } }, ["session_id"] = session });
                Say(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = tool, ["input"] = new JsonObject() } }, ["session_id"] = session });
                Say(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = input.ToJsonString() } }, ["session_id"] = session });
                Assistant(entry, [new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = tool, ["input"] = input.DeepClone() }]);

                if (Flag(call, "cutWhileAsked"))
                {
                    // It asks, and waits for the host's interrupt whatever the host answers.
                    cut = id;
                    cutAsk = Id("ask-");
                    Say(new JsonObject
                    {
                        ["type"] = "control_request",
                        ["request_id"] = cutAsk,
                        ["request"] = new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = tool, ["input"] = input.DeepClone(), ["tool_use_id"] = id },
                    });
                    return;
                }

                if (Flag(call, "calledUnasked"))
                {
                    // A call of the server's sent with no ask: its result is the server's answer.
                    CallServer(call, tool);
                    return;
                }

                if (Flag(call, "unasked") || Flag(call, "unaskedFailed") || Flag(call, "unaskedFailedMarked") || Flag(call, "unaskedFailedBare") || Flag(call, "invalidInput") || Flag(call, "refusedByClaude")
                    || Flag(call, "deniedByRule") || Flag(call, "deniedByRuleBare") || Flag(call, "deniedByRuleTold"))
                {
                    if (Flag(call, "deniedByRule") || Flag(call, "deniedByRuleBare"))
                    {
                        // As every version refuses a file tool's call its deny rules refuse: as it reads the input, the host
                        // never asked, nothing said but the result.
                        ToolResult(id, ok: false, DssHarness.Core.Agents.ClaudeCode.ClaudeLine.DeniedByRuleResult, Flag(call, "deniedByRule") ? Meta(id, null, "reject", "config", "rule") : null);
                    }
                    else if (Flag(call, "deniedByRuleTold"))
                    {
                        // Told twice: by a permission_denied line, as a search's refusal is, and by the file tool's result.
                        Say(new JsonObject { ["type"] = "system", ["subtype"] = "permission_denied", ["tool_name"] = tool, ["tool_use_id"] = id, ["decision_reason_type"] = "rule", ["message"] = "Permission to read the file has been denied.", ["session_id"] = session });
                        ToolResult(id, ok: false, DssHarness.Core.Agents.ClaudeCode.ClaudeLine.DeniedByRuleResult, Meta(id, "permission-rule", "reject", "config", "rule"));
                    }
                    else if (Flag(call, "refusedByClaude"))
                    {
                        // As a launch without --permission-prompt-tool stdio refuses a call: by itself, the host never asked.
                        const string Refused = "Claude requested permissions to use this tool, but you haven't granted it yet.";
                        Say(new JsonObject { ["type"] = "system", ["subtype"] = "permission_denied", ["tool_name"] = tool, ["tool_use_id"] = id, ["decision_reason_type"] = "rule", ["message"] = Refused, ["session_id"] = session });
                        ToolResult(id, ok: false, Refused, Meta(id, "user-rejected", "reject", "config"));
                    }
                    else if (Flag(call, "invalidInput"))
                    {
                        ToolResult(id, ok: false, "<tool_use_error>InputValidationError: the input did not read</tool_use_error>", null);
                    }
                    else if (Flag(call, "unaskedFailedBare"))
                    {
                        // As a version before 2.1.303 writes a call that ran and failed: nothing stamped beside it.
                        ToolResult(id, ok: false, "ran unasked", null);
                    }
                    else if (Flag(call, "unaskedFailedMarked"))
                    {
                        // A call that ran and failed as a tool's own error is written: under the input-error mark.
                        ToolResult(id, ok: false, "<tool_use_error>ran unasked</tool_use_error>", Meta(id, null, "accept", "config"));
                    }
                    else
                    {
                        ToolResult(id, ok: Flag(call, "unasked"), "ran unasked", Meta(id, null, "accept", "config"));
                    }

                    callAt++;
                    Play();
                    return;
                }

                waitingOn = Id("ask-");
                Say(new JsonObject
                {
                    ["type"] = "control_request",
                    ["request_id"] = waitingOn,
                    ["request"] = new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = tool, ["input"] = input, ["tool_use_id"] = id },
                });
                return;
            }

            if (Flag(entry, "waitForMessage") && !tookIn)
            {
                // It waits for a message written while it works and takes it into this turn, as Claude Code takes one
                // written while a call is asked.
                awaiting = true;
                return;
            }

            if (entry["declined"] is JsonValue declined)
            {
                // A message the model declined to write: its stop reason said where declinedBy names - the stream, the
                // message, the turn's end - or in all three, and the turn ended, unless it hangs once it has said it.
                var category = Text(declined) is { Length: > 0 } named ? named : null;
                var details = category is null ? null : new JsonObject { ["type"] = "refusal", ["category"] = category };
                var by = Text(entry["declinedBy"]);
                Say(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["model"] = Model(entry) } }, ["session_id"] = session });

                if (by is null or "stream")
                {
                    Say(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "refusal", ["stop_details"] = details?.DeepClone() } }, ["session_id"] = session });
                }

                var message = new JsonObject { ["id"] = Id("msg_"), ["model"] = Model(entry), ["role"] = "assistant", ["content"] = new JsonArray() };

                if (by is null or "message")
                {
                    message["stop_reason"] = "refusal";
                    message["stop_details"] = details?.DeepClone();
                }

                Say(new JsonObject { ["type"] = "assistant", ["message"] = message, ["session_id"] = session });

                if (Flag(entry, "hang"))
                {
                    return;
                }

                Result(entry, string.Empty, null, error: false, "success", "completed", []);
                return;
            }

            if (entry["hang"] is JsonValue hang && hang.GetValue<bool>())
            {
                // It waits for the host's interrupt, writing nothing more.
                return;
            }

            if (Text(entry["error"]) is { } error)
            {
                var said = Text(entry["text"]) ?? "Invalid API key · Fix external API key";
                Assistant(entry, [new JsonObject { ["type"] = "text", ["text"] = said }], error);
                Result(entry, said, null, error: true, "success", "api_error", []);
                return;
            }

            var text = Text(entry["text"]) ?? "done";
            Assistant(entry, [new JsonObject { ["type"] = "text", ["text"] = text }]);

            if (entry["exitAfter"] is JsonValue after)
            {
                // It ends the process once its answer is written, before the turn's result.
                exit = after.GetValue<int>();
                return;
            }

            if (Text(entry["subtype"]) is { } subtype)
            {
                Result(entry, null, null, error: true, subtype, Text(entry["terminal"]) ?? "completed", [Text(entry["errorText"]) ?? subtype]);
                return;
            }

            if (Text(entry["terminal"]) is { } terminal)
            {
                Result(entry, text, null, error: true, "success", terminal, []);
                return;
            }

            JsonNode? answer = null;

            if (schema)
            {
                answer = entry.ContainsKey("answer") ? entry["answer"]?.DeepClone() : new JsonObject { ["answer"] = "alpha" };
                var structured = Id("toolu_");
                Say(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["model"] = Model(entry) } }, ["session_id"] = session });
                Say(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = structured, ["name"] = "StructuredOutput", ["input"] = new JsonObject() } }, ["session_id"] = session });
                Say(new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = answer?.ToJsonString() ?? "null" } }, ["session_id"] = session });
                Assistant(entry, [new JsonObject { ["type"] = "tool_use", ["id"] = structured, ["name"] = "StructuredOutput", ["input"] = answer?.DeepClone() }]);
                ToolResult(structured, ok: true, "Structured output provided successfully", Meta(structured, null, "accept", "config"));
            }

            Result(entry, text, answer, error: false, "success", "completed", []);
        }

        // A call's result, with what Claude Code says of it beside: whether it ran, and how its permission was decided.
        void ToolResult(string id, bool ok, string content, JsonObject? meta)
        {
            var line = new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content, ["is_error"] = !ok }),
                },
                ["session_id"] = session,
            };

            if (meta is not null)
            {
                line["tool_result_meta"] = new JsonArray(meta);
            }

            Say(line);
        }

        // The next message taken, where one waits and no turn is under way: a turn of its own.
        void Next()
        {
            // A message taken for its turn as the interrupt came holds the turn: the rest wait behind it; and no turn begins
            // before the servers are connected.
            if (!connected || busy || late is not null || !queued.TryDequeue(out var taken))
            {
                return;
            }

            var (message, messageId) = taken;

            busy = true;
            turn++;
            playing = turn < turns.Count && turns[turn] is JsonObject entry ? entry : [];
            calls = playing["calls"] is JsonArray listed ? [.. listed.OfType<JsonObject>()] : [];
            callAt = 0;
            tookIn = false;

            if (!Flag(playing, "noInit"))
            {
                Say(new JsonObject
                {
                    ["type"] = "system",
                    ["subtype"] = "init",
                    ["session_id"] = session,
                    ["model"] = Text(playing["initModel"]) ?? model,
                    ["tools"] = playing["initTools"]?.DeepClone() ?? new JsonArray([.. tools.Concat(serverTools).Concat(schema ? ["StructuredOutput"] : []).Select(tool => (JsonNode)tool)]),
                    ["mcp_servers"] = playing["initMcpServers"]?.DeepClone() ?? new JsonArray([.. servers.Select(server => (JsonNode)new JsonObject { ["name"] = server, ["status"] = Text(first?["mcpStatus"]) ?? "connected", ["source"] = "sdk" })]),
                    ["apiKeySource"] = Text(playing["apiKeySource"]) ?? (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } ? "ANTHROPIC_API_KEY" : "none"),
                    ["claude_code_version"] = "fake",
                });
            }

            Echo(message, messageId);

            if (Text(playing["stderr"]) is { } said)
            {
                Console.Error.WriteLine(said);
                Console.Error.Flush();
            }

            if (cutNext)
            {
                // A turn the interrupt reached before it began: cut as it starts.
                cutNext = false;
                Result(playing, null, null, error: true, "error_during_execution", "aborted_streaming", ["[ede_diagnostic] result_type=user"]);
                return;
            }

            if (playing["controlRequest"] is JsonObject asked)
            {
                // A request of its own the host may answer no kind of: it is written, and not waited on.
                Say(new JsonObject { ["type"] = "control_request", ["request_id"] = Id("req-"), ["request"] = asked.DeepClone() });
            }

            if (playing["rateLimit"] is JsonObject limit)
            {
                Say(new JsonObject { ["type"] = "rate_limit_event", ["rate_limit_info"] = limit.DeepClone(), ["session_id"] = session });
            }

            if (playing["exit"] is JsonValue code)
            {
                exit = code.GetValue<int>();
                return;
            }

            if (playing["slowMs"] is JsonValue slowTurn)
            {
                // A turn that takes its time before it plays, reading nothing meanwhile.
                Thread.Sleep(slowTurn.GetValue<int>());
            }

            if (Text(playing["sessionFile"]) is { } project)
            {
                var directory = Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? ".", "projects", project);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, session + ".jsonl"), "{}\n");
            }

            Play();
        }

        if (!connected)
        {
            // Each server is connected as the launch starts, before anything is read: its initialize first.
            Mcp(servers[0], new JsonObject
            {
                ["method"] = "initialize",
                ["params"] = new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "fake" } },
                ["jsonrpc"] = "2.0",
                ["id"] = 0,
            }, "initialize");
        }

        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);

        while (input.ReadLine() is { } line)
        {
            if (record is not null)
            {
                File.AppendAllText(record, new JsonObject { ["in"] = line }.ToJsonString() + "\n", Utf8NoBom);
            }

            if (JsonNode.Parse(line) is not JsonObject node)
            {
                continue;
            }

            var kind = Text(node["type"]);

            if (first is not null && Flag(first, "noInitialize"))
            {
                // Not a host's child: it neither answers the host nor plays a turn.
                continue;
            }

            if (kind == "control_request" && Text(node["request"]?["subtype"]) == "initialize")
            {
                if (first?["initializeAfterMs"] is JsonValue starting)
                {
                    Thread.Sleep(starting.GetValue<int>());
                }

                Say(first is not null && Flag(first, "initializeError")
                    ? new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = Text(node["request_id"]), ["error"] = "no host is taken here" } }
                    : new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = Text(node["request_id"]), ["response"] = new JsonObject() } });
            }
            else if (kind == "control_request" && Text(node["request"]?["subtype"]) == "interrupt")
            {
                if ((playing ?? first)?["ignoreInterrupt"] is JsonValue ignore && ignore.GetValue<bool>())
                {
                    continue;
                }

                if ((playing ?? first) is { } refusing && Flag(refusing, "refuseInterrupt"))
                {
                    // It refuses the interrupt, and the turn it would have cut goes on.
                    Say(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = Text(node["request_id"]), ["error"] = "no interrupt is taken here" } });
                    continue;
                }

                // The receipt names each waiting message it dropped, by its id; one taken for its turn already is not named.
                var others = ((playing ?? first)?["receiptAlsoNames"] as JsonArray ?? []).Select(Text);
                var cancelled = new JsonArray([.. held.Concat(queued.Select(waiting => waiting.Id)).Concat(others).OfType<string>().Select(id => (JsonNode)id)]);
                Say(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = Text(node["request_id"]), ["response"] = new JsonObject { ["still_queued"] = new JsonArray(), ["cancelled"] = cancelled } } });
                held.Clear();
                queued.Clear();

                if (playing?["slowInterrupt"] is JsonValue slow)
                {
                    Thread.Sleep(slow.GetValue<int>());
                }

                if (busy)
                {
                    waitingOn = null;

                    if (cut is not null)
                    {
                        // As 2.1.296 answers a call cut while it was asked: the ask withdrawn, the call stamped as not run.
                        Say(new JsonObject { ["type"] = "control_cancel_request", ["request_id"] = cutAsk });
                        ToolResult(cut, ok: false, "The user doesn't want to proceed with this tool use. The tool use was rejected.", new JsonObject { ["id"] = cut, ["non_execution_kind"] = "user-rejected" });
                        Say(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "[Request interrupted by user for tool use]" }) }, ["session_id"] = session });
                        cut = null;
                        cutAsk = null;
                        Result(playing!, null, null, error: true, "error_during_execution", "aborted_tools", ["[ede_diagnostic] result_type=user"]);
                    }
                    else
                    {
                        Result(playing!, null, null, error: true, "error_during_execution", "aborted_streaming", ["[ede_diagnostic] result_type=user"]);
                    }
                }

                if (late is { } lateTaken)
                {
                    queued.Enqueue(lateTaken);
                    late = null;
                    cutNext = true;
                    Next();
                }
            }
            else if (kind == "user" && awaiting)
            {
                awaiting = false;
                tookIn = true;
                Echo(Text(node["message"]?["content"]) ?? string.Empty, Text(node["uuid"]));
                Play();
            }
            else if (kind == "user")
            {
                var next = turn + 1 + queued.Count + (late is null ? 0 : 1);
                var content = Text(node["message"]?["content"]) ?? string.Empty;
                var id = Text(node["uuid"]);
                var coming = next < turns.Count ? turns[next] as JsonObject : null;

                if (coming is not null && Flag(coming, "untaken"))
                {
                    // A message it never takes: no turn begins, and the next message is the next entry's.
                    turn++;
                    held.Add(id);
                    continue;
                }

                if (coming is not null && Flag(coming, "takenLate"))
                {
                    late = (content, id);
                    continue;
                }

                if (coming?["takeAfterMs"] is JsonValue after)
                {
                    // It takes its time before it takes the message, reading nothing meanwhile.
                    Thread.Sleep(after.GetValue<int>());
                }

                queued.Enqueue((content, id));
                Next();
            }
            else if (kind == "control_response" && Text(node["response"]?["request_id"]) is { } answeredId && awaited.Remove(answeredId, out var step))
            {
                // The host's answer to a message of the server's, written back as the CLI writes every answer.
                Say(node);
                var reply = node["response"]?["response"]?["mcp_response"];

                switch (step)
                {
                    case "initialize":
                        Mcp(servers[0], new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, "initialized");
                        break;
                    case "initialized":
                        Mcp(servers[0], new JsonObject { ["method"] = "tools/list", ["jsonrpc"] = "2.0", ["id"] = 1 }, "list");
                        break;
                    case "list":
                        serverTools.AddRange((reply?["result"]?["tools"] as JsonArray ?? []).Select(tool => $"mcp__{servers[0]}__{Text(tool?["name"])}"));
                        connected = true;
                        Next();
                        break;
                    case "call":
                        var served = calls[callAt];
                        var servedId = Text(served["id"])!;
                        var failed = Text(node["response"]?["subtype"]) == "error" ? Text(node["response"]?["error"]) : reply?["error"] is { } error ? $"MCP error {error["code"]}: {Text(error["message"])}" : null;
                        var said = failed ?? string.Join('\n', (reply?["result"]?["content"] as JsonArray ?? []).Select(block => Text(block?["text"])));
                        var isError = failed is not null || (reply?["result"]?["isError"] is JsonValue flagged && flagged.GetValue<bool>());
                        ToolResult(servedId, ok: !isError, said, Meta(servedId, null, "accept", "user_temporary"));
                        callAt++;
                        Play();
                        break;
                }
            }
            else if (kind == "control_response" && waitingOn is not null && Text(node["response"]?["request_id"]) == waitingOn)
            {
                // As the CLI does, the host's answer is written back on the stream.
                Say(node);
                waitingOn = null;
                var call = calls[callAt];
                var id = Text(call["id"])!;
                var tool = Text(call["tool"]) ?? "Read";
                var behavior = Text(node["response"]?["response"]?["behavior"]);

                if (Text(node["response"]?["subtype"]) == "error")
                {
                    ToolResult(id, ok: false, "Tool permission request failed: Error: " + Text(node["response"]?["error"]), Meta(id, "permission-rule", "reject", "config"));
                }
                else if (behavior == "allow" && tool.StartsWith("mcp__", StringComparison.Ordinal) && servers.Contains(tool.Split("__")[1]))
                {
                    // A call of the server's, allowed: sent to it, its result its answer.
                    CallServer(call, tool);
                    continue;
                }
                else if (behavior == "allow")
                {
                    ToolResult(id, ok: true, Text(call["result"]) ?? "ran", Meta(id, null, "accept", "user_temporary"));
                }
                else if (Flag(call, "calledThoughDenied") && tool.StartsWith("mcp__", StringComparison.Ordinal))
                {
                    // A call of the server's the host denied, sent to it all the same.
                    CallServer(call, tool);
                    continue;
                }
                else if (Flag(call, "ranThoughDenied"))
                {
                    ToolResult(id, ok: true, "ran all the same", Meta(id, null, "accept", "config"));
                }
                else
                {
                    ToolResult(id, ok: false, Text(node["response"]?["response"]?["message"]) ?? "denied", Meta(id, "permission-rule", "reject", "user_reject"));
                }

                callAt++;
                Play();
            }

            if (exit is { } code)
            {
                return code;
            }
        }

        if (turns.OfType<JsonObject>().Any(entry => Flag(entry, "ignoreEof")))
        {
            // A launch that does not end by itself once its input is closed: only ending it whole ends it.
            Thread.Sleep(Timeout.Infinite);
        }

        return 0;
    }

    /// <summary>The servers of type <c>sdk</c> an <c>--mcp-config</c> file names, by name; none where it names no file.</summary>
    private static List<string> McpServers(string? file)
        => file is null
            ? []
            : [.. (JsonNode.Parse(File.ReadAllText(file))?["mcpServers"] as JsonObject ?? []).Where(server => Text(server.Value?["type"]) == "sdk").Select(server => server.Key)];

    private static bool Flag(JsonObject entry, string name)
        => entry[name] is JsonValue value && value.TryGetValue<bool>(out var set) && set;

    /// <summary>What Claude Code says beside a call's result: why it did not run, where it did not, and how its permission was decided.</summary>
    private static JsonObject Meta(string id, string? notRun, string decision, string source, string? reason = null)
    {
        var meta = new JsonObject { ["id"] = id };

        if (notRun is not null)
        {
            meta["non_execution_kind"] = notRun;
        }

        meta["permission_decision"] = new JsonObject { ["decision"] = decision, ["source"] = source };

        if (reason is not null)
        {
            meta["permission_decision"]!["reason_type"] = reason;
        }

        return meta;
    }

    private static string? Option(string[] arguments, string name)
    {
        var at = Array.IndexOf(arguments, name);
        return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
