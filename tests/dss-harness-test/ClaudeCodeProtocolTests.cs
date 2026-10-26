using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.ClaudeCode;

namespace DssHarness.Tests;

/// <summary>
/// The recorded protocol of Claude Code, as the probe of part 03's slice 12 took it on every channel - the oldest version
/// this tool takes, the stable channel's and the latest: the curated recordings hold every line kind and every point the
/// probe settled, and the fake <c>claude</c> - built from them - asks, waits and ends its turns as the real one does.
/// Nothing here runs anything live or asks the network; the one live test that made the recordings is the machine's own
/// and never runs in continuous integration, where no login is.
/// </summary>
public sealed class ClaudeCodeProtocolTests
{
    /// <summary>The channels the probe recorded, each with the version it ran.</summary>
    public static TheoryData<string, string> Channels() => new()
    {
        { "oldest", "2.1.287" },
        { "stable", "2.1.292" },
        { "latest", "2.1.303" },
    };

    /// <summary>Every curated recording of every channel, by channel and file.</summary>
    public static TheoryData<string, string> CuratedFiles()
    {
        var data = new TheoryData<string, string>();

        foreach (var channel in new[] { "oldest", "stable", "latest" })
        {
            foreach (var file in Directory.GetFiles(Fixtures(channel, ""), "*.jsonl").Select(Path.GetFileName).Order(StringComparer.Ordinal))
            {
                data.Add(channel, file!);
            }
        }

        return data;
    }

    /// <summary>The built-in model ids, each on every channel.</summary>
    public static TheoryData<string, string> ModelsOnChannels()
    {
        var data = new TheoryData<string, string>();

        foreach (var channel in new[] { "oldest", "stable", "latest" })
        {
            foreach (var model in new[] { "claude-fable-5-1", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" })
            {
                data.Add(channel, model);
            }
        }

        return data;
    }

    /// <summary>
    /// Every channel's recording parses, and holds what a launch is: the tools as it names them - the schema's own
    /// <c>StructuredOutput</c> among them, the same on the oldest version as on the latest - the model <c>--model</c>
    /// was given, the version of the program, and <c>none</c> as the key source under the person's login.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheRecordingOfAChannel_HoldsWhatALaunchBeganAs(string channel, string version)
    {
        var began = Lines(channel, "shapes.jsonl").Select(ClaudeCodeProbe.BeganOf).OfType<ClaudeCodeProbe.Began>().ToList();

        Assert.NotEmpty(began);
        Assert.All(began, launch =>
        {
            Assert.Equal(version, launch.Version);
            Assert.Equal("claude-sonnet-5-5", launch.Model);
            Assert.Equal("none", launch.ApiKeySource);
            Assert.Equal(
                ["Bash", "Edit", "Glob", "Grep", "NotebookEdit", "Read", "StructuredOutput", "WebFetch", "WebSearch", "Write"],
                launch.Tools);
            Assert.Contains("interrupt_receipt_v1", launch.Capabilities, StringComparer.Ordinal);
        });
    }

    /// <summary>
    /// The curated recordings are the public repository's, so they keep nothing of the account or the machine that made
    /// them: every id a server gave and every uuid is a stable stand-in, the same one wherever the recording names the
    /// same thing, the account's own members of a rate limit are elided, and no path of the machine is left. A
    /// regeneration that forgot to scrub them is refused here.
    /// </summary>
    [Theory]
    [MemberData(nameof(CuratedFiles))]
    public void TheCuratedRecording_KeepsNothingOfTheAccountThatMadeIt(string channel, string file)
    {
        var text = File.ReadAllText(Fixtures(channel, file));

        // An id is a long run after its prefix, which a word of the protocol (msg_lifecycle_v1) is not.
        var ids = System.Text.RegularExpressions.Regex.Matches(text, @"\b(?:req|msg|toolu|srvtoolu)_[A-Za-z0-9]{16,}").Select(found => found.Value).ToList();
        var uuids = System.Text.RegularExpressions.Regex.Matches(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Select(found => found.Value).ToList();

        Assert.All(ids, id => Assert.Matches("^(req|msg|toolu|srvtoolu)_[0-9]{24}$", id));

        // ... and a word of the protocol is kept as written, never taken for an id and given a stand-in.
        Assert.DoesNotMatch(@"\b(?:req|msg|toolu|srvtoolu)_[0-9]{24}_v[0-9]", text);
        Assert.All(uuids, uuid => Assert.StartsWith("00000000-0000-4000-8000-", uuid, StringComparison.Ordinal));

        // No path of the machine: no drive, no home directory under any of its spellings.
        Assert.DoesNotMatch(@"(?i)\b[a-z]:\\\\|/Users/|/home/|~\\\\|~/", text);

        if (file == "findings.jsonl")
        {
            return;
        }

        // The stand-ins still pair up: every call a result or an ask names is a call a message made.
        var lines = ClaudeCodeProbe.Read(Fixtures(channel, file));
        var made = lines
            .Where(line => line.Kind == "assistant")
            .SelectMany(line => (line.Node["message"]?["content"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(block => block["type"]?.GetValue<string>() == "tool_use")
            .Select(block => block["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(lines.Select(ClaudeCodeProbe.AskOf).OfType<ClaudeCodeProbe.Ask>(), ask => Assert.Contains(ask.ToolUseId, made));
        // The account's own members, each where the version writes it - a seven-day window's event on 2.1.287 leaves out the
        // overage's two, 2.1.308 and 2.1.310 add whether it is enabled, and the later recordings say its usage as utilization.
        Assert.All(lines.Where(line => line.Kind == "rate_limit_event").Select(line => line.Node["rate_limit_info"]!.AsObject()), limit =>
        {
            Assert.Contains(limit, member => member.Key is "overageStatus" or "unifiedWindows" or "utilization");

            foreach (var member in new[] { "overageStatus", "overageDisabledReason", "overageEnabled", "unifiedWindows", "utilization" }.Where(limit.ContainsKey))
            {
                Assert.Equal("<elided: the account's own>", limit[member]!.GetValue<string>());
            }
        });
    }

    /// <summary>
    /// The scenario holds the five points on every channel: asks for every kind of call, the read-only <c>Grep</c> and
    /// <c>Glob</c> among them under their ask rules, their allowed results, a denial the host made and the result that
    /// counts it, an interrupt that ends the turn under way <c>error_during_execution</c>, a turn answered after it, and
    /// a call the silent host never answered, which runs nothing and fails once the host's input closes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheScenario_HoldsTheFivePoints(string channel, string version)
    {
        _ = version;
        var lines = Lines(channel, "shapes.jsonl");
        var reasons = lines.Select(ClaudeCodeProbe.ReasonOf).OfType<ClaudeCodeProbe.Reason>().ToList();
        var ends = lines.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>().ToList();
        var results = ClaudeCodeProbe.ToolResultsOf(lines);
        var found = ClaudeCodeProbe.FindingsOf(Fixtures(channel, "findings.jsonl"))["scenario"];

        // Every kind of call, each asked, in the scenario's order; a model may read a file once more before it writes it.
        Assert.Equal(
            ["Read", "Write", "Write", "Bash", "WebSearch", "Grep", "Glob", "Write", "Write", "Write", "Read", "Read"],
            reasons.Where(reason => !IsReadOf(reason, "settings.local.json")).Select(reason => reason.Tool));
        Assert.Equal("rule", reasons.Single(reason => reason.Tool == "Grep").ReasonType);
        Assert.Equal("rule", reasons.Single(reason => reason.Tool == "Glob").ReasonType);

        // (a) Two turns answered before anything else happened.
        Assert.Equal(["completed", "completed"], ends.Take(2).Select(end => end.TerminalReason));

        // (b) The interrupt ends the turn under way, an error of its own: no result, its diagnostic in errors.
        var aborted = ends.FindIndex(end => end.TerminalReason == "aborted_streaming");
        Assert.Equal(2, aborted);
        Assert.Equal("error_during_execution", ends[aborted].Subtype);
        Assert.True(ends[aborted].IsError);
        Assert.False(ends[aborted].HasResult);

        // (c) The turn after it completed, and every turn after that too.
        Assert.All(ends.Skip(aborted + 1), end => Assert.Equal("completed", end.TerminalReason));

        // (d) The denial the host made ran nothing and is counted by its turn's own result; the allowed calls ran.
        Assert.Contains("denied by the probe", results);
        Assert.Contains("marker=settings", results);
        Assert.Equal(1, ends[5].Denials);
        Assert.False(found["wroteDenied"]!.GetValue<bool>());
        Assert.True(found["wroteWritten"]!.GetValue<bool>());
        Assert.True(found["wroteRan"]!.GetValue<bool>());

        // (e) Nothing ran while the host was silent; the call failed only once the host's input closed.
        Assert.False(found["ranWhileHostSilent"]!.GetValue<bool>());
        Assert.Equal("Tool permission request failed: AbortError: Tool permission stream closed before response received", results[^2]);

        // A completed turn carries the schema's envelope twice: as text in `result`, and as the JSON it holds in `structured_output`.
        var completed = lines.Where(line => ClaudeCodeProbe.EndOf(line) is { TerminalReason: "completed" }).ToList();
        Assert.Equal(ends.Count(end => end.TerminalReason == "completed"), completed.Count);
        Assert.All(completed, line => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(line.Node["result"]!.GetValue<string>()), line.Node["structured_output"])));
    }

    /// <summary>
    /// A path Claude Code protects is not refused before the host: the write into <c>.git</c> and into the worktree's
    /// <c>.claude</c> is asked as a <c>safetyCheck</c>, the write outside the working directory as <c>workingDir</c>, and
    /// the host's allow runs each - no built-in rule denied anything the probe asked for.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void AProtectedPath_IsAskedOfTheHost_AndRunsOnItsAllow(string channel, string version)
    {
        _ = version;
        var reasons = Lines(channel, "shapes.jsonl").Select(ClaudeCodeProbe.ReasonOf).OfType<ClaudeCodeProbe.Reason>().ToList();
        var found = ClaudeCodeProbe.FindingsOf(Fixtures(channel, "findings.jsonl"))["scenario"];

        Assert.Equal("safetyCheck", reasons.Single(reason => IsWriteOf(reason, @".git\probe-protected.txt")).ReasonType);
        Assert.Equal("safetyCheck", reasons.Single(reason => IsWriteOf(reason, @".claude\settings.local.json")).ReasonType);
        Assert.Equal("workingDir", reasons.Single(reason => IsWriteOf(reason, "probe-outside.txt")).ReasonType);
        Assert.True(found["wroteProtected"]!.GetValue<bool>());
        Assert.True(found["wroteLocalSettings"]!.GetValue<bool>());
        Assert.True(found["wroteOutside"]!.GetValue<bool>());
    }

    /// <summary>
    /// An ask the host answers <c>error</c> fails the call with the host's own words, runs nothing, and is counted by the
    /// turn's result as a denial; the turn itself completes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void AnErrorAnswer_FailsTheCall_AndCountsADenial(string channel, string version)
    {
        _ = version;
        var lines = Lines(channel, "shapes.jsonl");
        var results = ClaudeCodeProbe.ToolResultsOf(lines);
        var failed = results.FindIndex(result => result == "Tool permission request failed: Error: the probe's host failed to decide");

        Assert.NotEqual(-1, failed);

        var ends = lines.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>().ToList();
        var errorTurn = ends[^2];

        Assert.Equal("completed", errorTurn.TerminalReason);
        Assert.Equal(1, errorTurn.Denials);
    }

    /// <summary>
    /// Under <c>--setting-sources user</c> nothing of the worktree reaches the model: its <c>CLAUDE.md</c> is not in the
    /// context and its skill is not loaded, while a skill of the user's own directory is - and with <c>project</c>
    /// among the sources, the worktree's skill is.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void UnderTheUsersSettingsAlone_NothingOfTheWorktreeIsRead(string channel, string version)
    {
        _ = version;
        var scenario = Lines(channel, "shapes.jsonl");

        Assert.Contains(scenario.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>(), end => end.TerminalReason == "completed");
        Assert.Contains("{\"answer\":\"none; no\"}", scenario.Where(line => line.Kind == "result" && line.Node["result"] is JsonValue).Select(line => line.Node["result"]!.GetValue<string>()));
        Assert.All(Skills(scenario), skill => Assert.DoesNotContain("probe-worktree-skill", skill, StringComparer.Ordinal));
        Assert.All(Skills(Lines(channel, "env-user.jsonl")), skill => Assert.Contains("probe-user-skill", skill, StringComparer.Ordinal));
        Assert.All(Skills(Lines(channel, "env-project.jsonl")), skill => Assert.Contains("probe-worktree-skill", skill, StringComparer.Ordinal));
    }

    /// <summary>
    /// The result subtypes beyond a completed turn and an interrupted one, as each launch ended: the turn limit, the
    /// budget (its ask left unanswered, and every turn after it ended at once), a model the provider does not know, and
    /// a full context - none of them carrying a <c>result</c> but the two that end <c>success</c> in error.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheOtherResults_EndAsRecorded(string channel, string version)
    {
        _ = version;
        var turnLimit = Ends(channel, "max-turns.jsonl");
        var budget = Ends(channel, "budget.jsonl");
        var unknown = Ends(channel, "unknown-model.jsonl");
        var full = Ends(channel, "full-context.jsonl");

        Assert.Equal(("error_max_turns", "max_turns", true, false), Shape(Assert.Single(turnLimit)));
        Assert.Equal(["Reached maximum number of turns (1)"], turnLimit[0].Errors);

        Assert.Equal(2, budget.Count);
        Assert.All(budget, end =>
        {
            Assert.Equal(("error_max_budget_usd", "budget_exhausted", true, false), Shape(end));
            Assert.Equal(["Reached maximum budget ($0.0001)"], end.Errors);
        });
        Assert.Single(Lines(channel, "budget.jsonl").Select(ClaudeCodeProbe.AskOf).OfType<ClaudeCodeProbe.Ask>());
        Assert.Empty(ClaudeCodeProbe.ToolResultsOf(Lines(channel, "budget.jsonl")));

        Assert.Equal(2, unknown.Count);
        Assert.All(unknown, end => Assert.Equal(("success", "api_error", true, true), Shape(end)));
        Assert.All(unknown, end => Assert.Equal(0m, end.CostUsd));
        Assert.Equal(["<synthetic>", "<synthetic>"], ClaudeCodeProbe.ModelsOf(Lines(channel, "unknown-model.jsonl")));

        Assert.Equal(["completed", "blocking_limit", "blocking_limit"], full.Select(end => end.TerminalReason));
        Assert.All(full.Skip(1), end => Assert.Equal(("success", "blocking_limit", true, true), Shape(end)));

        var tooLong = Lines(channel, "full-context.jsonl").Where(line => line.Kind == "assistant" && line.Node["error"] is not null).ToList();
        Assert.Equal(2, tooLong.Count);
        Assert.All(tooLong, line =>
        {
            Assert.Equal("invalid_request", line.Node["error"]!.GetValue<string>());
            Assert.Equal("Prompt is too long", line.Node["message"]!["content"]![0]!["text"]!.GetValue<string>());
        });
    }

    /// <summary>
    /// Only <c>--permission-prompt-tool stdio</c> sends an ask to the host: with it alone the read is asked and runs; with
    /// <c>--permission-prompts host</c> alone, with neither, and with <c>-p</c> and the latter alone, nothing is asked and
    /// the call is denied on the spot, a <c>system/permission_denied</c> line saying so.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void ThePermissionPromptTool_IsWhatSendsAnAskToTheHost(string channel, string version)
    {
        _ = version;
        var asked = Lines(channel, "prompt-tool-only.jsonl");

        Assert.Equal(["Read"], asked.Select(ClaudeCodeProbe.AskOf).OfType<ClaudeCodeProbe.Ask>().Select(ask => ask.Tool));
        Assert.Equal(0, Assert.Single(asked.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>()).Denials);

        foreach (var file in new[] { "prompts-host-only.jsonl", "no-prompt-flag.jsonl", "print-prompts-host-only.jsonl" })
        {
            var lines = Lines(channel, file);
            var denied = ClaudeCodeProbe.SystemOf(lines, "permission_denied");

            Assert.Empty(lines.Select(ClaudeCodeProbe.AskOf).OfType<ClaudeCodeProbe.Ask>());
            Assert.NotEmpty(denied);
            Assert.Equal("Read", denied[0]["tool_name"]!.GetValue<string>());
            Assert.Equal("rule", denied[0]["decision_reason_type"]!.GetValue<string>());
            Assert.Equal("Claude requested permissions to use Read, but you haven't granted it yet.", denied[0]["message"]!.GetValue<string>());
            Assert.Equal(denied.Count, Assert.Single(lines.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>()).Denials);
        }
    }

    /// <summary>
    /// An empty <c>fallbackModel</c> in <c>--settings</c> takes away the chain a lower settings file names: with the
    /// launch's settings naming no fallback, a model the provider does not know fails over to the lower file's model, a
    /// <c>system/model_fallback</c> line saying so; with them naming an empty one, the turn ends in the API error.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void AnEmptyFallbackModel_TakesTheChainBelowAway(string channel, string version)
    {
        _ = version;
        var below = Lines(channel, "fallback-below.jsonl");
        var emptied = Lines(channel, "fallback-emptied.jsonl");
        var fellBack = Assert.Single(ClaudeCodeProbe.SystemOf(below, "model_fallback"));

        Assert.Equal("model_not_found", fellBack["trigger"]!.GetValue<string>());
        Assert.Equal("claude-no-such-model-0-0", fellBack["original_model"]!.GetValue<string>());
        Assert.Equal("claude-haiku-5-5", fellBack["fallback_model"]!.GetValue<string>());
        Assert.All(ClaudeCodeProbe.ModelsOf(below), model => Assert.Equal("claude-haiku-5-5", model));
        Assert.Equal("completed", Assert.Single(below.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>()).TerminalReason);

        Assert.Empty(ClaudeCodeProbe.SystemOf(emptied, "model_fallback"));
        Assert.Equal(("success", "api_error", true, true), Shape(Assert.Single(emptied.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>())));
    }

    /// <summary>A model's id passed with <c>--model</c> is run as written: <c>system/init</c> and every assistant line name it.</summary>
    [Theory]
    [MemberData(nameof(ModelsOnChannels))]
    public void AModelId_IsRunAsWritten_AndNamedInInitAndEveryMessage(string channel, string model)
    {
        var lines = Lines(channel, model == "claude-sonnet-5-5" ? "shapes.jsonl" : $"model-{model}.jsonl");
        var models = ClaudeCodeProbe.ModelsOf(lines);

        Assert.All(lines.Select(ClaudeCodeProbe.BeganOf).OfType<ClaudeCodeProbe.Began>(), began => Assert.Equal(model, began.Model));
        Assert.NotEmpty(models);
        Assert.All(models, named => Assert.Equal(model, named));
        Assert.All(lines.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>(), end => Assert.NotEqual("api_error", end.TerminalReason));
    }

    /// <summary>
    /// With <c>-p</c> the launch speaks the same protocol streamed both ways - the initialize answer, the ask, a second
    /// message answered on the same stream - and does not end after its first result while its input is open.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void ThePrintMode_SpeaksTheSameProtocol(string channel, string version)
    {
        var lines = Lines(channel, "print.jsonl");
        var found = ClaudeCodeProbe.FindingsOf(Fixtures(channel, "findings.jsonl"))["print"];

        Assert.Equal(version, lines.Select(ClaudeCodeProbe.BeganOf).OfType<ClaudeCodeProbe.Began>().First().Version);
        Assert.Equal(["Read"], lines.Select(ClaudeCodeProbe.AskOf).OfType<ClaudeCodeProbe.Ask>().Select(ask => ask.Tool));
        Assert.Equal(["completed", "completed"], lines.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>().Select(end => end.TerminalReason));
        Assert.False(found["exitedAfterFirstResultWithInputOpen"]!.GetValue<bool>());
    }

    /// <summary>
    /// The host gone runs nothing: its input closed while a call waits fails the call and ends the turn, then the
    /// process, with 0; the host dead - its input and its reading end closed - leaves the stream at the ask, and the
    /// process ends on its own, the file never written.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheHostGone_RunsNothing(string channel, string version)
    {
        _ = version;
        var findings = ClaudeCodeProbe.FindingsOf(Fixtures(channel, "findings.jsonl"));
        var closed = Lines(channel, "input-closed.jsonl");

        Assert.Equal("Tool permission request failed: AbortError: Tool permission stream closed before response received", ClaudeCodeProbe.ToolResultsOf(closed)[0]);
        Assert.Equal("completed", closed.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>().Last().TerminalReason);
        Assert.False(findings["input-closed"]["wroteGone"]!.GetValue<bool>());
        Assert.True(findings["input-closed"]["exitedWithinSixtySecondsOfTheClose"]!.GetValue<bool>());
        Assert.Equal(0, findings["input-closed"]["exitCode"]!.GetValue<int>());

        var dead = Lines(channel, "host-dead.jsonl");

        Assert.NotNull(ClaudeCodeProbe.AskOf(dead[^1]));
        Assert.False(findings["host-dead"]["wroteGone"]!.GetValue<bool>());
        Assert.True(findings["host-dead"]["exitedWithinSixtySecondsOfTheDeath"]!.GetValue<bool>());
    }

    /// <summary>
    /// The <c>env</c> of <c>--settings</c> wins for a name another layer also sets - the process's own environment, the
    /// project's settings and the user's own - while the names it does not set still come from below; but a launch that
    /// inherits a desktop app's <c>CLAUDE_CODE_ENTRYPOINT</c> keeps the process's value.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheLaunchSettingsEnv_WinsOverEveryLayerBelow(string channel, string version)
    {
        _ = version;

        Assert.Contains("marker=settings", ClaudeCodeProbe.ToolResultsOf(Lines(channel, "shapes.jsonl")));
        Assert.Equal("marker=settings lower=project", ClaudeCodeProbe.ToolResultsOf(Lines(channel, "env-project.jsonl"))[0]);
        Assert.Equal("marker=settings lower=user", ClaudeCodeProbe.FindingsOf(Fixtures(channel, "findings.jsonl"))["env-user"]["hookSaw"]!.GetValue<string>());
        Assert.Equal("marker=process", ClaudeCodeProbe.ToolResultsOf(Lines(channel, "env-desktop-entrypoint.jsonl"))[0]);
    }

    /// <summary>
    /// <c>apiKeySource</c> says <c>none</c> under the person's login and says the same with no credential at all, where
    /// the turn ends in the provider's refusal; and no launch, with <c>-p</c> or without, wrote a session file.
    /// </summary>
    [Theory]
    [MemberData(nameof(Channels))]
    public void TheKeySource_SaysNoneUnderTheLoginAndWithout_AndNoSessionFileIsWritten(string channel, string version)
    {
        _ = version;
        var signedOut = Lines(channel, "env-user.jsonl");

        Assert.Equal("none", Assert.Single(signedOut.Select(ClaudeCodeProbe.BeganOf).OfType<ClaudeCodeProbe.Began>()).ApiKeySource);
        Assert.Equal("authentication_failed", signedOut.Single(line => line.Kind == "assistant").Node["error"]!.GetValue<string>());
        Assert.Equal(("success", "api_error", true, true), Shape(Assert.Single(signedOut.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>())));

        var findings = ClaudeCodeProbe.FindingsOf(Fixtures(channel, "findings.jsonl"));

        Assert.Equal(23, findings.Count);
        Assert.All(findings.Values, found => Assert.Equal("none", found["sessionFile"]!.GetValue<string>()));
    }

    /// <summary>
    /// The engine's tool server is served over the control channel to a host that is no SDK, on every channel (slice 22b3,
    /// recorded 2026-10-26 on 2.1.287, 2.1.308 and 2.1.310): an MCP server of type <c>sdk</c> that <c>--mcp-config</c>
    /// names is connected by <c>mcp_message</c>s before the launch's init - its initialize, its notification, its tool
    /// list - its tools stand in the init beside <c>--tools</c>, which named none of them, and the server is connected;
    /// each call of one is asked of the host first, by its ask rule, and only then sent as <c>tools/call</c>, naming in
    /// its <c>_meta</c> the call the host was asked about; the server's text is the call's result; and a call the host
    /// denies is never sent.
    /// </summary>
    [Theory]
    [InlineData("oldest", "2.1.287")]
    [InlineData("stable", "2.1.308")]
    [InlineData("latest", "2.1.310")]
    public void TheEnginesToolServer_IsServedOverTheControlChannel(string channel, string version)
    {
        var lines = Lines(channel, "mcp-message.jsonl");
        var init = lines.FindIndex(line => line.Kind == "system" && line.Node["subtype"]?.GetValue<string>() == "init");
        var messages = lines
            .Select((line, at) => (Line: line, At: at))
            .Where(pair => pair.Line.Kind == "control_request" && pair.Line.Node["request"]?["subtype"]?.GetValue<string>() == "mcp_message")
            .ToList();

        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "tools/call"], messages.Select(pair => pair.Line.Node["request"]!["message"]!["method"]!.GetValue<string>()));
        Assert.All(messages, pair => Assert.Equal("dssharness", pair.Line.Node["request"]!["server_name"]!.GetValue<string>()));
        Assert.All(messages.Take(3), pair => Assert.True(pair.At < init, $"{pair.Line.Node["request"]!["message"]!["method"]} came after the init"));

        var began = Assert.IsType<ClaudeCodeProbe.Began>(ClaudeCodeProbe.BeganOf(lines[init]));
        Assert.Equal(version, began.Version);
        Assert.Equal(["Glob", "Grep", "Read", "StructuredOutput", "mcp__dssharness__web_fetch", "mcp__dssharness__web_search"], began.Tools.Order(StringComparer.Ordinal));
        Assert.Equal("""[{"name":"dssharness","status":"connected","source":"sdk"}]""", lines[init].Node["mcp_servers"]!.ToJsonString());

        var asks = lines.Select((line, at) => (Ask: ClaudeCodeProbe.AskOf(line), At: at)).Where(pair => pair.Ask is not null).ToList();
        var (searched, askedAt) = Assert.Single(asks, pair => pair.Ask!.Tool == "mcp__dssharness__web_search");
        var call = messages[3];
        var parameters = call.Line.Node["request"]!["message"]!["params"]!;
        Assert.True(askedAt < call.At);
        Assert.Equal("""{"name":"dssharness","source":"sdk"}""", lines[askedAt].Node["request"]!["mcp_server"]!.ToJsonString());
        Assert.Equal(("web_search", searched!.ToolUseId), (parameters["name"]!.GetValue<string>(), parameters["_meta"]!["claudecode/toolUseId"]!.GetValue<string>()));
        Assert.Equal(lines[askedAt].Node["request"]!["input"]!.ToJsonString(), parameters["arguments"]!.ToJsonString());
        Assert.Contains("PROBE_MCP_TITLE_5c2e", Assert.Single(ClaudeCodeProbe.ToolResultsOf(lines), result => result.Contains("PROBE_MCP", StringComparison.Ordinal)), StringComparison.Ordinal);

        // The fetch the host denied: asked, and never sent.
        var (_, deniedAt) = Assert.Single(asks, pair => pair.Ask!.Tool == "mcp__dssharness__web_fetch");
        Assert.DoesNotContain(messages, pair => pair.At > deniedAt);

        var found = ClaudeCodeProbe.FindingsOf(Fixtures(channel, "findings.jsonl"))["mcp-message"];
        Assert.Equal((true, true, 0, true), (found["eachCallAskedFirst"]!.GetValue<bool>(), found["askedAboutTheDeniedCall"]!.GetValue<bool>(), found["callsAfterTheDenial"]!.GetValue<int>(), found["toldTheResult"]!.GetValue<bool>()));
    }

    /// <summary>
    /// A call Claude Code's own deny rules refuse is never asked of the host, on every channel (recorded 2026-10-26 on
    /// 2.1.287, 2.1.308 and 2.1.310): a file tool's - a read, a write - is refused as Claude Code reads the call's input and
    /// told by its result alone, the same words on every version, a permission decision <c>reject</c> by a <c>rule</c>
    /// beside it from 2.1.308 and nothing on 2.1.287; a search's and a command's by a <c>system/permission_denied</c> line
    /// besides, and their results stamped <c>permission-rule</c>. Each reads as a call that did not run, and the file
    /// tools' as refused by a rule.
    /// </summary>
    [Theory]
    [InlineData("oldest", false)]
    [InlineData("stable", true)]
    [InlineData("latest", true)]
    public void ACallClaudeCodesOwnDenyRulesRefuse_IsNeverAsked_AndReadsAsRefusedByARule(string channel, bool stampsTheDecision)
    {
        var lines = Lines(channel, "deny-rules.jsonl");
        var calls = lines
            .Where(line => line.Kind == "assistant")
            .SelectMany(line => (line.Node["message"]?["content"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(block => block["type"]?.GetValue<string>() == "tool_use" && block["name"]?.GetValue<string>() != "StructuredOutput")
            .Select(block => (Id: block["id"]!.GetValue<string>(), Tool: block["name"]!.GetValue<string>()))
            .ToList();
        var results = lines
            .Select(line => ClaudeLine.Read(line.Node.ToJsonString()))
            .OfType<ClaudeLine.User>()
            .SelectMany(user => user.Results.Select(result => (Result: result, Meta: (user.Node["tool_result_meta"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(meta => meta["id"]?.GetValue<string>() == result.CallId))))
            .ToDictionary(pair => pair.Result.CallId, StringComparer.Ordinal);
        var refusals = lines.Select(line => ClaudeLine.Read(line.Node.ToJsonString())).OfType<ClaudeLine.Refusal>().ToList();

        Assert.Equal(["Read", "Grep", "Write", "Bash"], calls.Select(call => call.Tool));
        Assert.Empty(lines.Select(ClaudeCodeProbe.AskOf).OfType<ClaudeCodeProbe.Ask>());

        foreach (var (id, tool) in calls)
        {
            var (result, meta) = results[id];
            Assert.False(result.Ran, $"{tool} read as a call that ran");
            Assert.False(result.Ok);

            if (tool is "Read" or "Write")
            {
                Assert.Equal(ClaudeLine.DeniedByRuleResult, result.Content);
                Assert.True(result.RefusedByRule, $"{tool} did not read as refused by a rule");
                Assert.DoesNotContain(refusals, refusal => refusal.CallId == id);
                Assert.Equal(stampsTheDecision ? """{"decision":"reject","source":"config","reason_type":"rule"}""" : null, meta?["permission_decision"]?.ToJsonString());
                Assert.Null(meta?["non_execution_kind"]);
            }
            else
            {
                Assert.Equal(tool, Assert.Single(refusals, refusal => refusal.CallId == id).Tool);
                Assert.Equal("permission-rule", meta?["non_execution_kind"]?.GetValue<string>());
            }
        }

        var found = ClaudeCodeProbe.FindingsOf(Fixtures(channel, "findings.jsonl"))["deny-rules"];
        Assert.False(found["wroteWritten"]!.GetValue<bool>());
    }

    /// <summary>
    /// The fake answers the initialize handshake with the recorded envelope, asks about the read, and runs it when
    /// the host allows - ending the turn completed, the schema's envelope in the result, no denial counted.
    /// </summary>
    [Fact]
    public async Task TheFakeAsksAndWaits_AnAllowRunsTheRead_AndTheTurnEndsCompleted()
    {
        using var child = Fake();
        await WriteAsync(child, """
            {"type":"control_request","request_id":"host-1","request":{"subtype":"initialize"}}
            """);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"init\"", StringComparison.Ordinal));

        await WriteAsync(child, """
            {"type":"user","message":{"role":"user","content":"read a.txt"}}
            """);
        var asked = await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        Assert.Contains("\"tool_name\":\"Read\"", asked, StringComparison.Ordinal);

        await WriteAsync(child, Allow);
        var ended = await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        // Every line the fake said is a line of JSON, as the recording's are: a line that cannot be
        // parsed is a shape a reader of the stream would choke on, whatever the test asked.
        lock (child.Seen)
        {
            Assert.All(child.Seen, line => Assert.NotNull(JsonNode.Parse(line)));
        }

        Assert.Contains("\"terminal_reason\":\"completed\"", ended, StringComparison.Ordinal);
        Assert.Contains("\"permission_denials\":[]", ended, StringComparison.Ordinal);

        // The envelope twice, as the recording carries it: text in `result`, its JSON in `structured_output`.
        Assert.Contains("\"result\":\"{\\\"answer\\\":\\\"alpha\\\"}\"", ended, StringComparison.Ordinal);
        Assert.Contains("\"structured_output\":{\"answer\":\"alpha\"}", ended, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deny does not run the read, and the turn's own result counts the denial - the denial's entry naming the
    /// tool and the call it held, as the recording shows.
    /// </summary>
    [Fact]
    public async Task TheFakeAsksAndWaits_ADenyRunsNothing_AndCountsTheDenial()
    {
        using var child = Fake();
        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        await WriteAsync(child, """
            {"type":"control_response","response":{"subtype":"success","request_id":"00000000-0000-4000-8000-000000000002","response":{"behavior":"deny","message":"denied by the host"}}}
            """);
        var ended = await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        Assert.Contains("\"terminal_reason\":\"completed\"", ended, StringComparison.Ordinal);
        Assert.Contains("\"permission_denials\":[{\"tool_name\":\"Read\"", ended, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", ended, StringComparison.Ordinal);
    }

    /// <summary>
    /// The interrupt ends the turn under way: the answer acknowledges it, and the turn's result is the error the
    /// recording shows - <c>error_during_execution</c> at <c>aborted_streaming</c>, its diagnostic in <c>errors</c> and
    /// no <c>result</c>.
    /// </summary>
    [Fact]
    public async Task TheFakeEndsItsTurn_OnTheInterrupt()
    {
        using var child = Fake();
        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        await WriteAsync(child, """
            {"type":"control_request","request_id":"host-2","request":{"subtype":"interrupt"}}
            """);
        var ended = ClaudeCodeProbe.EndOf(new ClaudeCodeProbe.Line("result", JsonNode.Parse(await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal)))!))!;

        Assert.Equal(("error_during_execution", "aborted_streaming", true, false), Shape(ended));
    }

    /// <summary>
    /// A call with the host silent is waited for: nothing more is written, as long as the silence holds - and an
    /// answer in a shape the recording does not know is the same silence, which is what the real child does to a
    /// flat envelope (<c>probe.md</c>: a flat shape is not heard at all). Each shape names the ask and an allow, so
    /// only the envelope, read as JSON, tells them from the one that is heard.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"control_response","response_to":"00000000-0000-4000-8000-000000000002","response":{"behavior":"allow"}}""")]
    [InlineData("""{"type":"control_response","request_id":"00000000-0000-4000-8000-000000000002","behavior":"allow"}""")]
    [InlineData("""{"type":"control_response","response":{"request_id":"00000000-0000-4000-8000-000000000002","behavior":"allow"}}""")]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"00000000-0000-4000-8000-000000000002","behavior":"allow"}}""")]
    [InlineData("""{"type":"control_response","response":{"request_id":"00000000-0000-4000-8000-000000000002","response":{"behavior":"allow"}}}""")]
    public async Task TheFakeWaitsWhileTheHostIsSilent_AndDoesNotHearAShapeItDoesNotKnow(string shape)
    {
        using var child = Fake();
        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        await WriteAsync(child, shape);

        var after = await ReadForAsync(child, TimeSpan.FromSeconds(2));

        Assert.DoesNotContain("\"type\":\"result\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"tool_result\"", after, StringComparison.Ordinal);
    }

    /// <summary>The fake runs the model <c>--model</c> names, as written, and names it in <c>system/init</c> and in every message.</summary>
    [Fact]
    public async Task TheFakeNamesTheModelItWasGiven_InItsInitAndEveryMessage()
    {
        using var child = Fake(["--model", "claude-haiku-5-5", "--permission-prompt-tool", "stdio", "--permission-prompts", "host"]);
        await WriteAsync(child, """
            {"type":"control_request","request_id":"host-1","request":{"subtype":"initialize"}}
            """);
        var began = await UntilAsync(child, line => line.Contains("\"subtype\":\"init\"", StringComparison.Ordinal));

        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));
        await WriteAsync(child, Allow);
        await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        Assert.Equal("claude-haiku-5-5", ClaudeCodeProbe.BeganOf(new ClaudeCodeProbe.Line("system", JsonNode.Parse(began)!))!.Model);

        lock (child.Seen)
        {
            Assert.Equal(
                ["claude-haiku-5-5", "claude-haiku-5-5"],
                ClaudeCodeProbe.ModelsOf(child.Seen.Select(line => new ClaudeCodeProbe.Line(JsonNode.Parse(line)!["type"]?.GetValue<string>() ?? "result", JsonNode.Parse(line)!))));
        }
    }

    /// <summary>
    /// Without <c>--permission-prompt-tool stdio</c> the fake puts nothing to the host - <c>--permission-prompts host</c>
    /// alone is not enough, as the probe measured on every version: the read is denied on the spot, a
    /// <c>system/permission_denied</c> line says so, and the turn's result counts the denial.
    /// </summary>
    [Fact]
    public async Task TheFakeAsksNothing_WithoutThePermissionPromptTool()
    {
        using var child = Fake(["--model", "claude-sonnet-5-5", "--permission-prompts", "host"]);
        await WriteAsync(child, ReadMessage);
        var ended = await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        lock (child.Seen)
        {
            Assert.DoesNotContain(child.Seen, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));
            Assert.Single(child.Seen, line => line.Contains("\"subtype\":\"permission_denied\"", StringComparison.Ordinal));
        }

        Assert.Contains("\"permission_denials\":[{\"tool_name\":\"Read\"", ended, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ask the host answers <c>error</c> fails the call with the host's own words and runs nothing; the turn
    /// completes, its result counting the failed call as a denial.
    /// </summary>
    [Fact]
    public async Task TheFakeFailsTheCall_WhenTheHostAnswersError()
    {
        using var child = Fake();
        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        await WriteAsync(child, """
            {"type":"control_response","response":{"subtype":"error","request_id":"00000000-0000-4000-8000-000000000002","error":"the host failed"}}
            """);
        var failed = await UntilAsync(child, line => line.Contains("\"type\":\"tool_result\"", StringComparison.Ordinal));
        var ended = await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        Assert.Contains("\"content\":\"Tool permission request failed: Error: the host failed\",\"is_error\":true", failed, StringComparison.Ordinal);
        Assert.Contains("\"terminal_reason\":\"completed\"", ended, StringComparison.Ordinal);
        Assert.Contains("\"permission_denials\":[{\"tool_name\":\"Read\"", ended, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", ended, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host's input closing while a call waits fails the call - nothing runs - ends the turn around it, and then
    /// the process, with 0, as the real child does.
    /// </summary>
    [Fact]
    public async Task TheFakeFailsTheWaitingCall_AndExits_WhenItsInputCloses()
    {
        using var child = Fake();
        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        child.Process.StandardInput.Close();
        var failed = await UntilAsync(child, line => line.Contains("\"type\":\"tool_result\"", StringComparison.Ordinal));
        var ended = await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));
        await child.Process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("\"content\":\"Tool permission request failed: AbortError: Tool permission stream closed before response received\",\"is_error\":true", failed, StringComparison.Ordinal);
        Assert.Contains("\"terminal_reason\":\"completed\"", ended, StringComparison.Ordinal);
        Assert.Equal(0, child.Process.ExitCode);
    }

    /// <summary>
    /// <c>--max-turns 1</c> ends the turn whose allowed call would need a further one: <c>error_max_turns</c> at
    /// <c>max_turns</c>, its reason in <c>errors</c> and no <c>result</c> - and the process, its last turn an error, exits 1.
    /// </summary>
    [Fact]
    public async Task TheFakeEndsAtTheTurnLimit()
    {
        using var child = Fake(Launch("--max-turns", "1"));
        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));
        await WriteAsync(child, Allow);
        var ended = await EndedAsync(child);

        Assert.Equal(("error_max_turns", "max_turns", true, false), Shape(ended));
        Assert.Equal(["Reached maximum number of turns (1)"], ended.Errors);

        child.Process.StandardInput.Close();
        await child.Process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, child.Process.ExitCode);
    }

    /// <summary>
    /// <c>--max-budget-usd</c> below a turn's first request ends that turn while its ask still waits - the read never
    /// runs, an answer after it is not heard - and every turn after it ends at once, the same way.
    /// </summary>
    [Fact]
    public async Task TheFakeEndsAtTheBudget_WithItsAskUnanswered_AndEveryTurnAfter()
    {
        using var child = Fake(Launch("--max-budget-usd", "0.0001"));
        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));
        var first = await EndedAsync(child);

        await WriteAsync(child, Allow);
        await WriteAsync(child, ReadMessage);
        var second = await EndedAsync(child);

        Assert.All(new[] { first, second }, ended =>
        {
            Assert.Equal(("error_max_budget_usd", "budget_exhausted", true, false), Shape(ended));
            Assert.Equal(["Reached maximum budget ($0.0001)"], ended.Errors);
        });

        lock (child.Seen)
        {
            Assert.DoesNotContain(child.Seen, line => line.Contains("\"type\":\"tool_result\"", StringComparison.Ordinal));
            Assert.Single(child.Seen, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A window lowered by <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c> fills: the turn that would pass it ends at
    /// <c>blocking_limit</c>, Claude Code's own message saying the prompt is too long, and every turn after it ends the
    /// same way - nothing is compacted.
    /// </summary>
    [Fact]
    public async Task TheFakeEndsAtTheBlockingLimit_AndEveryTurnAfter()
    {
        using var child = Fake(Launch(), new Dictionary<string, string>(StringComparer.Ordinal) { ["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] = "15000" });
        await WriteAsync(child, ReadMessage);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));
        await WriteAsync(child, Allow);
        var first = await EndedAsync(child);

        await WriteAsync(child, ReadMessage);
        var tooLong = await UntilAsync(child, line => line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal));
        var second = await EndedAsync(child);
        await WriteAsync(child, ReadMessage);
        var third = await EndedAsync(child);

        Assert.Equal("completed", first.TerminalReason);
        Assert.Contains("\"text\":\"Prompt is too long\"", tooLong, StringComparison.Ordinal);
        Assert.Contains("\"error\":\"invalid_request\"", tooLong, StringComparison.Ordinal);
        Assert.All(new[] { second, third }, ended => Assert.Equal(("success", "blocking_limit", true, true), Shape(ended)));
    }

    /// <summary>
    /// A model the provider does not know ends each turn in an API error before anything is asked: Claude Code's own
    /// message, <c>model_not_found</c>, and a <c>success</c> result that is an error at <c>api_error</c>.
    /// </summary>
    [Fact]
    public async Task TheFakeEndsInAnApiError_ForAModelTheProviderDoesNotKnow()
    {
        using var child = Fake(["--model", "claude-no-such-model-0-0", "--permission-prompt-tool", "stdio", "--permission-prompts", "host"]);
        await WriteAsync(child, ReadMessage);
        var refused = await UntilAsync(child, line => line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal));
        var ended = await EndedAsync(child);

        Assert.Contains("\"error\":\"model_not_found\"", refused, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"<synthetic>\"", refused, StringComparison.Ordinal);
        Assert.Equal(("success", "api_error", true, true), Shape(ended));

        lock (child.Seen)
        {
            Assert.DoesNotContain(child.Seen, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));
        }
    }

    /// <summary>The user message every fake test sends.</summary>
    private const string ReadMessage = """{"type":"user","message":{"role":"user","content":"read a.txt"}}""";

    /// <summary>The host's allow of the fake's one ask, in the recorded envelope.</summary>
    private const string Allow = """{"type":"control_response","response":{"subtype":"success","request_id":"00000000-0000-4000-8000-000000000002","response":{"behavior":"allow","updatedInput":{"file_path":"a.txt"}}}}""";

    /// <summary>One curated recording's file.</summary>
    private static string Fixtures(string channel, string file) => ClaudeCodeProbe.Fixtures(channel, file);

    /// <summary>One curated recording's lines.</summary>
    private static List<ClaudeCodeProbe.Line> Lines(string channel, string file) => ClaudeCodeProbe.Read(Fixtures(channel, file));

    /// <summary>How each turn of one curated recording ended.</summary>
    private static List<ClaudeCodeProbe.Ended> Ends(string channel, string file)
        => [.. Lines(channel, file).Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>()];

    /// <summary>A result's shape: its subtype, why it ended, whether in error, and whether it carries a <c>result</c>.</summary>
    private static (string Subtype, string TerminalReason, bool IsError, bool HasResult) Shape(ClaudeCodeProbe.Ended ended)
        => (ended.Subtype, ended.TerminalReason, ended.IsError, ended.HasResult);

    /// <summary>The skills each <c>system/init</c> of a recording lists.</summary>
    private static List<List<string>> Skills(List<ClaudeCodeProbe.Line> lines)
        => [.. lines
            .Where(line => ClaudeCodeProbe.BeganOf(line) is not null)
            .Select(line => (line.Node["skills"] as JsonArray ?? []).Select(skill => skill!.GetValue<string>()).ToList())];

    /// <summary>Whether an ask is a write whose path ends in <paramref name="suffix"/>.</summary>
    private static bool IsWriteOf(ClaudeCodeProbe.Reason reason, string suffix)
        => reason.Tool == "Write" && (reason.Input?["file_path"]?.GetValue<string>() ?? "").EndsWith(suffix, StringComparison.Ordinal);

    /// <summary>Whether an ask is a read whose path ends in <paramref name="suffix"/>.</summary>
    private static bool IsReadOf(ClaudeCodeProbe.Reason reason, string suffix)
        => reason.Tool == "Read" && (reason.Input?["file_path"]?.GetValue<string>() ?? "").EndsWith(suffix, StringComparison.Ordinal);

    /// <summary>The engine's launch flags for the fake, with <paramref name="more"/> after them.</summary>
    private static string[] Launch(params string[] more)
        => ["--model", "claude-sonnet-5-5", "--permission-prompt-tool", "stdio", "--permission-prompts", "host", .. more];

    /// <summary>Starts the fake <c>claude</c>, this assembly as a child in its <c>claude-code</c> mode, with the engine's launch flags.</summary>
    private static FakeClaude Fake() => Fake(Launch());

    /// <summary>Starts the fake <c>claude</c> with <paramref name="arguments"/> and, beside its mode, <paramref name="environment"/>.</summary>
    private static FakeClaude Fake(string[] arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = TestHost.StartOf(TestHost.ChildRequest("claude-code", arguments));

        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.CreateNoWindow = true;

        foreach (var (variable, value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            start.Environment[variable] = value;
        }

        return new FakeClaude(Process.Start(start)!);
    }

    /// <summary>Writes one line to the fake's input.</summary>
    private static async Task WriteAsync(FakeClaude child, string line)
    {
        await child.Process.StandardInput.WriteLineAsync(line.AsMemory(), TestContext.Current.CancellationToken);
        await child.Process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits for the next result the fake says, and reads it.</summary>
    private static async Task<ClaudeCodeProbe.Ended> EndedAsync(FakeClaude child)
    {
        var line = await UntilAsync(child, said => said.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        return ClaudeCodeProbe.EndOf(new ClaudeCodeProbe.Line("result", JsonNode.Parse(line)!))!;
    }

    /// <summary>Waits for the next line that holds <paramref name="what"/>, failing the test where none comes.</summary>
    private static async Task<string> UntilAsync(FakeClaude child, Func<string, bool> what)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));

        while (true)
        {
            var line = await child.Lines.Reader.ReadAsync(bound.Token);

            lock (child.Seen)
            {
                child.Seen.Add(line);
            }

            if (what(line))
            {
                return line;
            }
        }
    }

    /// <summary>Reads all that comes inside <paramref name="bound"/>, and returns it as one text.</summary>
    private static async Task<string> ReadForAsync(FakeClaude child, TimeSpan bound)
    {
        _ = Task.Run(async () =>
        {
            while (await child.Lines.Reader.WaitToReadAsync(CancellationToken.None))
            {
                while (child.Lines.Reader.TryRead(out var line))
                {
                    lock (child.Seen)
                    {
                        child.Seen.Add(line);
                    }
                }
            }
        });

        await Task.Delay(bound, TestContext.Current.CancellationToken);

        lock (child.Seen)
        {
            return string.Join("\n", child.Seen);
        }
    }

    /// <summary>The fake child and what it has said.</summary>
    private sealed class FakeClaude : IDisposable
    {
        public FakeClaude(Process process)
        {
            Process = process;

            _ = Task.Run(async () =>
            {
                string? line;

                while ((line = process.StandardOutput.ReadLine()) is not null)
                {
                    await Lines.Writer.WriteAsync(line);
                }

                Lines.Writer.TryComplete();
            });
        }

        /// <summary>Everything the fake has said so far, safe to read while it goes on saying.</summary>
        public List<string> Seen { get; } = [];

        /// <summary>The lines as they come.</summary>
        public System.Threading.Channels.Channel<string> Lines { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

        public Process Process { get; }

        public void Dispose()
        {
            try
            {
                Process.StandardInput.Close();
                Process.WaitForExit(2_000);
            }
            catch (IOException)
            {
                // Already gone.
            }

            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(2_000);
            }

            Process.Dispose();
        }
    }
}
