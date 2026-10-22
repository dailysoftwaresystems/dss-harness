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
        var ids = 0;

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

        var queued = new Queue<string>();
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
                ["stop_reason"] = "end_turn",
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

                if (call["unasked"] is JsonValue unasked && unasked.GetValue<bool>())
                {
                    ToolResult(id, ok: true, "ran unasked");
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
                ToolResult(structured, ok: true, "Structured output provided successfully");
            }

            Result(entry, text, answer, error: false, "success", "completed", []);
        }

        void ToolResult(string id, bool ok, string content)
            => Say(new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content, ["is_error"] = !ok }),
                },
                ["session_id"] = session,
            });

        // The next message taken, where one waits and no turn is under way: a turn of its own.
        void Next()
        {
            if (busy || !queued.TryDequeue(out var message))
            {
                return;
            }

            busy = true;
            turn++;
            playing = turn < turns.Count && turns[turn] is JsonObject entry ? entry : [];
            calls = playing["calls"] is JsonArray listed ? [.. listed.OfType<JsonObject>()] : [];
            callAt = 0;

            Say(new JsonObject
            {
                ["type"] = "system",
                ["subtype"] = "init",
                ["session_id"] = session,
                ["model"] = Text(playing["initModel"]) ?? model,
                ["tools"] = new JsonArray([.. tools.Concat(schema ? ["StructuredOutput"] : []).Select(tool => (JsonNode)tool)]),
                ["apiKeySource"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } ? "ANTHROPIC_API_KEY" : "none",
                ["claude_code_version"] = "fake",
            });
            Say(new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = message },
                ["session_id"] = session,
                ["isReplay"] = true,
            });

            if (playing["exit"] is JsonValue code)
            {
                exit = code.GetValue<int>();
                return;
            }

            if (Text(playing["sessionFile"]) is { } project)
            {
                var directory = Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? ".", "projects", project);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, session + ".jsonl"), "{}\n");
            }

            Play();
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

            if (kind == "control_request" && Text(node["request"]?["subtype"]) == "initialize")
            {
                Say(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = Text(node["request_id"]), ["response"] = new JsonObject() } });
            }
            else if (kind == "control_request" && Text(node["request"]?["subtype"]) == "interrupt")
            {
                if (playing?["ignoreInterrupt"] is JsonValue ignore && ignore.GetValue<bool>())
                {
                    continue;
                }

                Say(new JsonObject { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = Text(node["request_id"]), ["response"] = new JsonObject() } });
                queued.Clear();

                if (playing?["slowInterrupt"] is JsonValue slow)
                {
                    Thread.Sleep(slow.GetValue<int>());
                }

                if (busy)
                {
                    waitingOn = null;
                    Result(playing!, null, null, error: true, "error_during_execution", "aborted_streaming", ["[ede_diagnostic] result_type=user"]);
                }
            }
            else if (kind == "user")
            {
                queued.Enqueue(Text(node["message"]?["content"]) ?? string.Empty);
                Next();
            }
            else if (kind == "control_response" && waitingOn is not null && Text(node["response"]?["request_id"]) == waitingOn)
            {
                waitingOn = null;
                var call = calls[callAt];
                var id = Text(call["id"])!;
                var behavior = Text(node["response"]?["response"]?["behavior"]);

                if (Text(node["response"]?["subtype"]) == "error")
                {
                    ToolResult(id, ok: false, "Tool permission request failed: Error: " + Text(node["response"]?["error"]));
                }
                else if (behavior == "allow")
                {
                    ToolResult(id, ok: true, Text(call["result"]) ?? "ran");
                }
                else
                {
                    ToolResult(id, ok: false, Text(node["response"]?["response"]?["message"]) ?? "denied");
                }

                callAt++;
                Play();
            }

            if (exit is { } code)
            {
                return code;
            }
        }

        return 0;
    }

    private static string? Option(string[] arguments, string name)
    {
        var at = Array.IndexOf(arguments, name);
        return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
