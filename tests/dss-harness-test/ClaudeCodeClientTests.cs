using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Runners;

namespace DssHarness.Tests;

/// <summary>
/// The Claude Code client over the fake <c>claude</c> played from a script: every launch a new session streamed both
/// ways by the one argument list, the engine its host - the calls it asks answered from the agent's policy, an answer
/// that does not read sent back, a message given while it works, a handoff asked when the run pauses, every message held
/// to the model asked for, a key the launch reports invalid passed over, and a session file it left removed.
/// </summary>
public sealed class ClaudeCodeClientTests : IDisposable
{
    private const string Model = "claude-sonnet-5-5";

    private const string Schema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""";

    /// <summary>
    /// How long a test's run may take: a client that never ends - or one that leaves a launch running that it should have
    /// ended - fails its test, and never holds the suite.
    /// </summary>
    private static readonly TimeSpan RunBound = TimeSpan.FromSeconds(120);

    private readonly CancellationTokenSource _bound = Bounded();

    private CancellationToken Token => _bound.Token;

    public void Dispose()
    {
        // A run the bound ended returns what it had found all the same: the bound reached is the failure.
        var reached = _bound.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested;
        _bound.Dispose();

        if (reached)
        {
            throw new TimeoutException($"the test's run reached its {RunBound.TotalSeconds:0} s bound: a launch was left running");
        }
    }

    private static CancellationTokenSource Bounded()
    {
        var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(RunBound);

        return bound;
    }

    /// <summary>
    /// A launch is a new session, streamed both ways, by the whole argument list and nothing else: no <c>-p</c>, the prompt
    /// never an argument but the first message on its input after the <c>initialize</c> request, a session id the engine
    /// made and a new one for every launch, no session kept, every root but the working directory an <c>--add-dir</c>; it
    /// runs in the agent's working directory, with the settings and the system prompt it was given beside the stream.
    /// </summary>
    [Fact]
    public async Task ALaunch_IsANewSessionStreamedBothWays_ByTheOneArgumentList_InTheAgentsWorkingDirectory()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);

        var first = await kit.Client().RunAsync(kit.Run(new AgentOutput("o", Schema)), new Sink(), Token);
        var second = await kit.Client().RunAsync(kit.Run(new AgentOutput("o", Schema)), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Completed, first.Status);
        Assert.Equal("""{"answer":"alpha"}""", first.StructuredOutput);

        var launches = kit.Launches();
        Assert.Equal(2, launches.Count);

        var argv = launches[0].Arguments;
        var session = Value(argv, "--session-id");
        Assert.True(Guid.TryParse(session, out _), session);
        Assert.NotEqual(session, Value(launches[1].Arguments, "--session-id"));
        Assert.Equal(first.Session, session);

        Assert.Equal(
            [
                "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--replay-user-messages", "--session-id", session, "--no-session-persistence", "--model", Model,
                "--effort", "high", "--max-turns", "30", "--max-budget-usd", "2.5",
                "--permission-mode", "default", "--permission-prompt-tool", "stdio", "--permission-prompts", "host",
                "--tools", "Read,Grep,Glob,Write,Edit,NotebookEdit,Bash", "--setting-sources", "user", "--strict-mcp-config",
                "--settings", Path.Combine(kit.StepDirectory, ClaudeLaunch.SettingsFileName),
                "--append-system-prompt-file", Path.Combine(kit.StepDirectory, ClaudeLaunch.SystemFileName),
                "--json-schema", Schema,
                "--add-dir", kit.WorkDirectory, "--add-dir", kit.Temp.Path,
            ],
            argv);
        Assert.DoesNotContain("-p", argv);
        Assert.DoesNotContain(argv, argument => argument is "--resume" or "--continue" or "--fallback-model" or "--print");
        Assert.DoesNotContain(argv, argument => argument.Contains("do the work", StringComparison.Ordinal));

        PathAssert.Same(kit.Worktree, launches[0].WorkingDirectory);
        Assert.Equal("be terse", File.ReadAllText(Path.Combine(kit.StepDirectory, ClaudeLaunch.SystemFileName)));

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(kit.StepDirectory, ClaudeLaunch.SettingsFileName)))!;
        Assert.Equal(
            ["Read", "Grep", "Glob", "Write", "Edit", "NotebookEdit", "Bash"],
            settings["permissions"]!["ask"]!.AsArray().Select(rule => rule!.GetValue<string>()));
        Assert.Empty(settings["fallbackModel"]!.AsArray());

        var read = launches[0].Read;
        Assert.Equal("initialize", read[0]["request"]?["subtype"]?.GetValue<string>());
        Assert.Equal("user", read[1]["type"]?.GetValue<string>());
        Assert.Equal("do the work", read[1]["message"]?["content"]?.GetValue<string>());
    }

    /// <summary>
    /// What a launch is given of the environment: nothing of a Claude Code session the engine was started in - the fake
    /// started as an engine started from the desktop app would hand it on, under <c>CLAUDE_CODE_ENTRYPOINT=claude-desktop</c>,
    /// sees none of it - no name that says who it is but what its way of signing in gives, no name the configuration
    /// appoints, the names the engine controls, and the Claude Code directory it reads - under a key, the entry's key and
    /// the provider's address. Every other name of the tool's own reaches it as it was.
    /// </summary>
    [Fact]
    public async Task TheEnvironment_HoldsWhatItsWayOfSigningInGives_AndNothingOfAHostSession()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        kit.Own["CLAUDE_CODE_ENTRYPOINT"] = "claude-desktop";
        kit.Own["CLAUDE_CODE_SSE_PORT"] = "51234";
        kit.Own["CLAUDECODE"] = "1";
        kit.Own["claude_code_entrypoint_lower"] = "claude-desktop";
        kit.Own["ClaudeCode"] = "1";
        kit.Own["ZAI_API_KEY"] = "zk-ambient";
        kit.Own["DSSHARNESS_KEPT_NAME"] = "kept";
        var config = Directory.CreateDirectory(kit.Temp.Combine("claude-config")).FullName;
        var run = kit.Run(credential: new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-work")], "https://gateway.example", config)) with
        {
            Withheld = ["ZAI_API_KEY", "WORK_KEY"],
        };

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var request = Assert.Single(kit.Runner.Requests);
        Assert.Equal((StreamKept.TailOfWholeLines, StreamKept.Tail, true), (request.OutputKept, request.ErrorKept, request.CutLine is not null));

        var given = request.Environment;
        Assert.Null(given["CLAUDE_CODE_ENTRYPOINT"]);
        Assert.Null(given["CLAUDE_CODE_SSE_PORT"]);
        Assert.Null(given["CLAUDECODE"]);

        // A host session's name in another case is the same variable on Windows, and is taken away in every spelling.
        Assert.Null(given["claude_code_entrypoint_lower"]);
        Assert.Null(given["ClaudeCode"]);
        Assert.Null(given["ZAI_API_KEY"]);
        Assert.False(given.ContainsKey("DSSHARNESS_KEPT_NAME"));
        Assert.Equal("sk-work", given["ANTHROPIC_API_KEY"]);
        Assert.Equal("https://gateway.example", given["ANTHROPIC_BASE_URL"]);
        Assert.Null(given["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Null(given["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal(config, given["CLAUDE_CONFIG_DIR"]);

        foreach (var (name, value) in ClaudeSettings.Controlled)
        {
            Assert.Equal(value, given[name]);
        }

        var seen = Assert.Single(kit.Launches()).Environment;
        Assert.False(seen.ContainsKey("CLAUDE_CODE_ENTRYPOINT"), "a host session's name reached the launch");
        Assert.False(seen.ContainsKey("CLAUDE_CODE_SSE_PORT"), "a host session's name reached the launch");
        Assert.False(seen.ContainsKey("CLAUDECODE"), "a host session's name reached the launch");
        Assert.DoesNotContain(seen.Keys, name => name.Equals("claude_code_entrypoint_lower", StringComparison.OrdinalIgnoreCase) || name.Equals("ClaudeCode", StringComparison.OrdinalIgnoreCase));
        Assert.False(seen.ContainsKey("ZAI_API_KEY"), "a withheld name reached the launch");
        Assert.Equal("kept", seen["DSSHARNESS_KEPT_NAME"]);
        Assert.Equal("sk-work", seen["ANTHROPIC_API_KEY"]);
        Assert.Equal(config, seen[ClaudeTranscripts.ConfigDirectoryVariable]);

        foreach (var (name, value) in ClaudeSettings.Controlled)
        {
            Assert.Equal(value, seen[name]);
        }
    }

    /// <summary>
    /// A launch that signs in with a token is given it under the name Claude Code reads that token from, whatever name the
    /// slot held it under, and no key beside it.
    /// </summary>
    [Theory]
    [InlineData(AgentAuth.OAuthToken, "CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData(AgentAuth.AuthToken, "ANTHROPIC_AUTH_TOKEN")]
    public async Task ALaunchSignedInWithAToken_IsGivenItUnderTheNameClaudeCodeReads(string auth, string read)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        kit.Own["ANTHROPIC_API_KEY"] = "sk-ambient";
        kit.Own[read] = "ambient";
        kit.Own["GATEWAY_TOKEN"] = "tok-1";

        var result = await kit.Client().RunAsync(kit.Run(credential: new AgentCredential(auth, [new AgentKey("GATEWAY_TOKEN", "tok-1")])) with { Withheld = ["GATEWAY_TOKEN"] }, new Sink(), Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var seen = Assert.Single(kit.Launches()).Environment;
        Assert.Equal("tok-1", seen[read]);
        Assert.False(seen.ContainsKey("ANTHROPIC_API_KEY"), "an ambient key reached a launch that signs in with a token");
        Assert.False(seen.ContainsKey("GATEWAY_TOKEN"));
    }

    /// <summary>
    /// Under the machine's login a launch is given no key, whatever the tool's own environment holds: the identity names
    /// are taken from it, so an ambient key never bills in place of the login.
    /// </summary>
    [Fact]
    public async Task ALaunchUnderTheLogin_IsGivenNoKey()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);

        await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        var given = Assert.Single(kit.Runner.Requests).Environment;

        foreach (var name in ClaudeLaunch.IdentityNames)
        {
            Assert.Null(given[name]);
        }

        Assert.False(given.ContainsKey(ClaudeTranscripts.ConfigDirectoryVariable));
    }

    /// <summary>
    /// Every call the launch asks about is answered from the agent's policy: one allowed runs as asked, its input handed
    /// back whole; one the policy refuses is denied with its rule's reason, which the model reads, and is a denial in the
    /// events, in <c>denials.jsonl</c> and in the result; a tool the launch was not given is refused by name.
    /// </summary>
    [Fact]
    public async Task ACallAllowed_RunsAsAsked_AndOneThePolicyRefuses_IsDeniedWithItsRuleAndReason()
    {
        using var kit = new Kit();
        var outside = kit.Temp.Combine("elsewhere", "x.txt");
        kit.Script(
        [
            new JsonObject
            {
                ["calls"] = new JsonArray(
                    new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" }, ["result"] = "1\talpha" },
                    new JsonObject { ["tool"] = "Write", ["input"] = new JsonObject { ["file_path"] = outside, ["content"] = "x" } },
                    new JsonObject { ["tool"] = "Bash", ["input"] = new JsonObject { ["command"] = "sudo rm -rf /" } },
                    new JsonObject { ["tool"] = "Agent", ["input"] = new JsonObject { ["prompt"] = "go" } }),
            },
        ]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(4, result.ToolCalls);
        Assert.Equal(3, result.Denials);

        var answers = Assert.Single(kit.Launches()).Read
            .Where(line => line["type"]?.GetValue<string>() == "control_response")
            .Select(line => line["response"]!["response"]!)
            .ToList();
        Assert.Equal(4, answers.Count);
        Assert.Equal("allow", answers[0]["behavior"]!.GetValue<string>());
        Assert.Equal("a.txt", answers[0]["updatedInput"]!["file_path"]!.GetValue<string>());
        Assert.All(answers.Skip(1), answer => Assert.Equal("deny", answer["behavior"]!.GetValue<string>()));
        Assert.Equal(
            "'Agent' is no tool this agent was given; it was given: Read, Grep, Glob, Write, Edit, NotebookEdit, Bash",
            answers[3]["message"]!.GetValue<string>());

        var denied = sink.Of<PermissionDenied>();
        Assert.Equal(["Write", "Bash", "Agent"], denied.Select(denial => denial.Tool));
        Assert.Equal(outside, denied[0].Subject);
        Assert.Equal("sudo rm -rf /", denied[1].Subject);
        Assert.Equal(denied.Select(denial => denial.Reason), answers.Skip(1).Select(answer => answer["message"]!.GetValue<string>()));

        var lines = File.ReadAllLines(Path.Combine(kit.StepDirectory, ClaudeLaunch.DenialsFileName));
        Assert.Equal(3, lines.Length);
        Assert.Equal("Bash", JsonNode.Parse(lines[1])!["tool"]!.GetValue<string>());

        var finished = sink.Of<ToolCallFinished>();
        Assert.Equal([true, false, false, false], finished.Select(call => call.Ok));
        Assert.Equal("1\talpha", finished[0].Result);
        Assert.Equal("""{"file_path":"a.txt"}""", finished[0].Input);
    }

    /// <summary>
    /// The stored stream is written as the launch runs, by the runner from the client's events: the launch's start, the
    /// prompt, the turn, the call and its result, the message, the turn's end, in that order, no delta among them; the
    /// control messages both ways are kept beside it.
    /// </summary>
    [Fact]
    public async Task TheStoredStream_IsWrittenAsTheLaunchRuns_AndTheControlMessagesBesideIt()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" } }) }]);
        var result = await kit.StepRunner().RunAsync(kit.Step(), null, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var kinds = File.ReadAllLines(Path.Combine(kit.StepDirectory, "stream.jsonl"))
            .Select(line => JsonNode.Parse(line)!["kind"]!.GetValue<string>())
            .ToList();
        Assert.Equal(
            [
                "runStarted", "messageSent", "sessionStarted", "turnStarted", "toolCallStarted", "assistantMessage",
                "toolCallFinished", "assistantMessage", "turnFinished", "usageReported", "runFinished",
            ],
            kinds);

        var control = File.ReadAllLines(Path.Combine(kit.StepDirectory, ClaudeLaunch.ControlFileName))
            .Select(line => JsonNode.Parse(line)!)
            .Select(line => $"{line["type"]!.GetValue<string>()}:{(line["request"]?["subtype"] ?? line["response"]?["subtype"])!.GetValue<string>()}")
            .ToList();
        Assert.Equal(["control_request:initialize", "control_response:success", "control_request:can_use_tool", "control_response:success"], control);
    }

    /// <summary>
    /// An answer that does not read against its schema is sent back to the same launch with each problem by its path, as
    /// a repair message, and the answer that then reads completes the step.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatDoesNotRead_IsSentBack_AndTheOneThatReadsCompletesTheStep()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["answer"] = new JsonObject { ["answers"] = "a" } }, new JsonObject { ["answer"] = new JsonObject { ["answer"] = "b" } }]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(new AgentOutput("o", Schema)), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("""{"answer":"b"}""", result.StructuredOutput);

        var repair = Assert.Single(sink.Of<OutputRepair>());
        Assert.Equal(1, repair.Attempt);
        Assert.Equal(["$: 'answer' is required and missing", "$.answers: is said by no property of the schema"], repair.Problems);

        var sent = sink.Of<MessageSent>();
        Assert.Equal([AgentMessageKind.FirstPrompt, AgentMessageKind.Repair], sent.Select(message => message.Which));
        Assert.Equal(
            "Your answer does not read against the schema it was asked for:\n"
                + "- $: 'answer' is required and missing\n"
                + "- $.answers: is said by no property of the schema\n"
                + "Answer again, whole, as the schema says.",
            sent[1].Text);
        Assert.Equal(2, result.Turns);
    }

    /// <summary>An answer still wrong after its repairs ends the step <see cref="AgentRunStatus.InvalidOutput"/>.</summary>
    [Fact]
    public async Task AnAnswerStillWrongAfterItsRepairs_EndsInvalidOutput()
    {
        using var kit = new Kit();
        var wrong = new JsonObject { ["answer"] = new JsonObject { ["answers"] = "a" } };
        kit.Script([wrong.DeepClone().AsObject(), wrong.DeepClone().AsObject(), wrong.DeepClone().AsObject()]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(new AgentOutput("o", Schema)), sink, Token);

        Assert.Equal(AgentRunStatus.InvalidOutput, result.Status);
        Assert.Equal(2, sink.Of<OutputRepair>().Count);
        var problems = StructuredOutputGate.Problems(new AgentOutput("o", Schema), wrong["answer"]);
        Assert.Equal($"the answer did not read against its schema after 2 repairs: {string.Join("; ", problems)}", result.Error);
    }

    /// <summary>
    /// A launch whose own schema retries are spent ends <see cref="AgentRunStatus.InvalidOutput"/> with no further
    /// message: Claude Code has already asked as often as it will.
    /// </summary>
    [Fact]
    public async Task ALaunchWhoseOwnSchemaRetriesAreSpent_EndsInvalidOutput_WithNoFurtherMessage()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["subtype"] = "error_max_structured_output_retries" }]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(new AgentOutput("o", Schema)), sink, Token);

        Assert.Equal(AgentRunStatus.InvalidOutput, result.Status);
        Assert.Single(sink.Of<MessageSent>());
        Assert.Empty(sink.Of<OutputRepair>());
    }

    /// <summary>
    /// Each limit a launch ended on is the step's ending: its turns, its budget, its context, each by the status it is.
    /// </summary>
    [Theory]
    [InlineData("error_max_turns", null, AgentRunStatus.MaxTurns, "error_max_turns")]
    [InlineData("error_max_budget_usd", null, AgentRunStatus.BudgetSpent, "Claude Code ended the launch on its --max-budget-usd: error_max_budget_usd")]
    [InlineData(null, "prompt_too_long", AgentRunStatus.ContextExhausted, "done")]
    [InlineData("error_during_execution", null, AgentRunStatus.Failed, "error_during_execution")]
    public async Task ALimitALaunchEndedOn_IsTheStepsEnding(string? subtype, string? terminal, AgentRunStatus status, string error)
    {
        using var kit = new Kit();
        var turn = new JsonObject();

        if (subtype is not null)
        {
            turn["subtype"] = subtype;
        }

        if (terminal is not null)
        {
            turn["terminal"] = terminal;
        }

        kit.Script([turn]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        // What the launch said of its end, as its result's errors or else its text, is what the step's error says.
        Assert.Equal((status, error), (result.Status, result.Error));
    }

    /// <summary>
    /// What the engine tells an agent at work is written to the same launch, a message of its own, and the result of the
    /// turn it came during is not the step's answer: the launch took the message after it, and the answer is the turn's
    /// that follows.
    /// </summary>
    [Fact]
    public async Task AMessageToTheAgentAtWork_IsGivenInTheSameLaunch_AndTheResultBeforeItIsNotTheAnswer()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["text"] = "before" }, new JsonObject { ["text"] = "after the update" }]);
        var run = kit.Run();
        run.Messages.Post("The plan this work follows was updated while you worked.");
        var sink = new Sink();

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("after the update", result.FinalText);
        Assert.Equal([AgentMessageKind.FirstPrompt, AgentMessageKind.PlanUpdate], sink.Of<MessageSent>().Select(message => message.Which));
        Assert.Single(kit.Launches());
    }

    /// <summary>
    /// A handoff asked while the agent works interrupts its turn, asks it for the handoff in the same launch, and ends
    /// <see cref="AgentRunStatus.HandedOff"/> with its answer; a call it asks for in that turn is denied with the turn's
    /// own sentence.
    /// </summary>
    [Fact]
    public async Task AHandoffAsked_InterruptsTheTurn_AsksTheAgent_AndEndsHandedOffWithItsAnswer()
    {
        using var kit = new Kit();
        kit.Script(
        [
            new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" } }), ["hang"] = true },
            new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "b.txt" } }), ["text"] = "Done: read a.txt." },
        ]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is ToolCallFinished)
            {
                run.Handoff.Ask("the run is pausing", "The plan changed.");
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.Equal(AgentRunStatus.HandedOff, result.Status);
        Assert.Equal("Done: read a.txt.", result.Handoff?.Answer);
        Assert.Single(sink.Of<HandoffAsked>());
        Assert.Equal(new HandoffAnswered(true), Assert.Single(sink.Of<HandoffAnswered>()));

        var question = sink.Of<MessageSent>().Single(message => message.Which == AgentMessageKind.HandoffQuestion);
        Assert.Equal($"The plan changed.\n\n{AgentHandoffSignal.Prompt}", question.Text);

        var read = Assert.Single(kit.Launches()).Read;
        Assert.Contains(read, line => line["request"]?["subtype"]?.GetValue<string>() == "interrupt" && line["request"]!["cancel_queued"]!.GetValue<bool>());

        var denial = Assert.Single(sink.Of<PermissionDenied>());
        Assert.Equal((AgentHandoffSignal.TurnRule, AgentHandoffSignal.ToolRefusal, "b.txt"), (denial.Rule, denial.Reason, denial.Subject));
    }

    /// <summary>
    /// A handoff turn that ends on an error - the conversation taking no more turns - ends the run
    /// <see cref="AgentRunStatus.HandedOff"/> at once, saying how it ended, and the agent is not asked again.
    /// </summary>
    [Fact]
    public async Task AHandoffTurnThatEndsOnAnError_HandsOffAtOnce_SayingHow()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true }, new JsonObject { ["terminal"] = "blocking_limit" }]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.Equal(AgentRunStatus.HandedOff, result.Status);
        Assert.Null(result.Handoff?.Answer);
        Assert.Equal("the handoff turn ended ContextExhausted: done", result.Handoff?.WhyNone);
        Assert.Single(sink.Of<MessageSent>(), message => message.Which == AgentMessageKind.HandoffQuestion);
        Assert.Equal(new HandoffAnswered(false, result.Handoff?.WhyNone), Assert.Single(sink.Of<HandoffAnswered>()));
    }

    /// <summary>
    /// A launch that never takes the interrupt is ended whole once its bound passes, and the run ends
    /// <see cref="AgentRunStatus.HandedOff"/> with why no agent answered: the handoff is the engine's to write.
    /// </summary>
    [Fact]
    public async Task AnInterruptNeverTaken_EndsTheLaunch_AndLeavesTheHandoffToTheEngine()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true, ["ignoreInterrupt"] = true }]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.Equal(AgentRunStatus.HandedOff, result.Status);
        Assert.Null(result.Handoff?.Answer);
        Assert.Equal("the agent did not take the interrupt within 10 s", result.Handoff?.WhyNone);
        Assert.Equal(new HandoffAnswered(false, "the agent did not take the interrupt within 10 s"), Assert.Single(sink.Of<HandoffAnswered>()));
    }

    /// <summary>
    /// The interrupt's bound is for the interrupt alone: a launch that took it within the bound is waited on for the turn
    /// it cuts, however long that takes within the handoff's own bound, and the agent is asked.
    /// </summary>
    [Fact]
    public async Task AnInterruptTaken_IsWaitedOnForTheTurnItCuts_PastItsOwnBound()
    {
        using var kit = new Kit();
        kit.Script(
        [
            new JsonObject { ["hang"] = true, ["slowInterrupt"] = (int)(ClaudeCodeClient.InterruptBound.TotalMilliseconds + 2000) },
            new JsonObject { ["text"] = "Done: nothing yet." },
        ]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.Equal(AgentRunStatus.HandedOff, result.Status);
        Assert.Equal("Done: nothing yet.", result.Handoff?.Answer);
    }

    /// <summary>
    /// A message written by a model the engine did not ask for ends the step <see cref="AgentRunStatus.WrongModel"/>,
    /// naming both - whether the launch says so as it starts or a later message does.
    /// </summary>
    [Theory]
    [InlineData("initModel", "its launch")]
    [InlineData("model", "its turn 1")]
    public async Task AMessageWrittenByAnotherModel_EndsTheStepWrongModel_NamingBoth(string member, string where)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { [member] = "claude-opus-5-5" }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(AgentRunStatus.WrongModel, result.Status);
        Assert.Equal(
            $"the step asked for '{Model}' and {where} was written by 'claude-opus-5-5'; a step's model is its configuration's and the engine's to choose, and neither chose this one",
            result.Error);
    }

    /// <summary>
    /// A launch that ends by itself before its turn's result fails the step saying so, at once: nothing it never reads is
    /// waited on.
    /// </summary>
    [Fact]
    public async Task ALaunchThatEndsByItself_FailsTheStep_AtOnce()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["exit"] = 3 }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.StartsWith("Claude Code ended before its turn's result, exit 3", result.Error, StringComparison.Ordinal);
    }

    /// <summary>A launch that runs past the step's duration is ended whole, and the step ends <see cref="AgentRunStatus.TimedOut"/>.</summary>
    [Fact]
    public async Task ALaunchThatRunsPastItsDuration_EndsTimedOut()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true }]);
        var run = kit.Run() with { Limits = new AgentLimits(30, 2.5m, TimeSpan.FromSeconds(3), null) };

        var result = await kit.Client().RunAsync(run, new Sink(), Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.Equal(AgentRunStatus.TimedOut, result.Status);
        Assert.Equal("the launch ran past its 00:00:03", result.Error);
    }

    /// <summary>
    /// The call an answer held to the schema arrives by is no call: it is neither counted nor told, and the answer is the
    /// result's.
    /// </summary>
    [Fact]
    public async Task TheCallAnAnswerArrivesBy_IsNoCall()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(new AgentOutput("o", Schema)), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(0, result.ToolCalls);
        Assert.Empty(sink.Of<ToolCallStarted>());
        Assert.Empty(sink.Of<ToolCallFinished>());
        Assert.Empty(sink.Of<ToolCallInputDelta>());
        Assert.All(sink.Of<AssistantMessage>(), message => Assert.Empty(message.Calls));
    }

    /// <summary>
    /// A call's result larger than an event carries is kept whole in a file beside the stream, and its event carries its
    /// head and its tail with what was left out between them, counted exactly.
    /// </summary>
    [Fact]
    public async Task AResultLargerThanAnEventCarries_IsKeptWholeBesideTheStream()
    {
        using var kit = new Kit();
        var whole = string.Concat(Enumerable.Range(0, ClaudeCodeClient.ResultCap / 8).Select(at => at.ToString("D8", System.Globalization.CultureInfo.InvariantCulture))) + "tail-end";
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" }, ["result"] = whole }) }]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var finished = Assert.Single(sink.Of<ToolCallFinished>());
        var file = Path.Combine(kit.StepDirectory, ClaudeLaunch.ToolResultsDirectoryName, finished.Id + ".txt");
        Assert.Equal(file, finished.ResultFile);
        Assert.Equal(whole, File.ReadAllText(file));

        const int Head = ClaudeCodeClient.ResultCap / 4;
        Assert.Equal(
            $"{whole[..Head]}\n[{whole.Length - ClaudeCodeClient.ResultCap} characters left out: the whole result is '{file}']\n{whole[^(ClaudeCodeClient.ResultCap - Head)..]}",
            finished.Result);
    }

    /// <summary>A model's id with a date after it is that model, and another model is not.</summary>
    [Theory]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5", true)]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5-20261001", true)]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5-preview", false)]
    [InlineData("claude-sonnet-5-5", "claude-opus-5-5", false)]
    public void AModelsIdWithADateAfterIt_IsThatModel(string asked, string named, bool same)
        => Assert.Equal(same, ClaudeCodeClient.Same(asked, named));

    /// <summary>
    /// A launch that signs in with a key is given its entry's key; a key Claude Code reports invalid is passed over, said
    /// as a move to the next key, and the next key is given a new launch with the step's prompt.
    /// </summary>
    [Fact]
    public async Task AKeyReportedInvalid_IsPassedOver_AndTheNextKeyIsGivenANewLaunch()
    {
        using var kit = new Kit();
        kit.Script(new JsonObject
        {
            ["byKey"] = new JsonObject
            {
                ["sk-one"] = new JsonArray(new JsonObject { ["error"] = "authentication_failed" }),
                ["sk-two"] = new JsonArray(new JsonObject { ["text"] = "served" }),
            },
        });
        var sink = new Sink();
        var credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("KEY_ONE", "sk-one"), new AgentKey("KEY_TWO", "sk-two")], "https://api.example");

        var result = await kit.Client().RunAsync(kit.Run(credential: credential), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("served", result.FinalText);
        Assert.Equal(["KEY_ONE"], result.KeysRefused);

        var launches = kit.Launches();
        Assert.Equal(2, launches.Count);
        Assert.Equal(["sk-one", "sk-two"], launches.Select(launch => launch.Environment["ANTHROPIC_API_KEY"]));
        Assert.NotEqual(Value(launches[0].Arguments, "--session-id"), Value(launches[1].Arguments, "--session-id"));
        Assert.Equal("do the work", launches[1].Read[1]["message"]?["content"]?.GetValue<string>());

        var moved = Assert.Single(sink.Of<FallbackStarted>());
        Assert.Equal(("KEY_ONE", "KEY_TWO"), (moved.FromKey, moved.ToKey));
        Assert.Equal(Model, moved.ToModel);
    }

    /// <summary>Where every key is refused, the step fails naming them all, each passed over.</summary>
    [Fact]
    public async Task EveryKeyRefused_FailsTheStep_NamingEach()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["error"] = "authentication_failed" }]);
        var credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("KEY_ONE", "sk-one"), new AgentKey("KEY_TWO", "sk-two")]);

        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(credential: credential), sink, Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal("Claude Code reported the key KEY_TWO invalid: Invalid API key · Fix external API key", result.Error);
        Assert.Equal(["KEY_ONE", "KEY_TWO"], result.KeysRefused);
        Assert.Equal(2, kit.Launches().Count);

        // One move, from the first key to the last: nothing is left to move to after it.
        var moved = Assert.Single(sink.Of<FallbackStarted>());
        Assert.Equal(("KEY_ONE", "KEY_TWO"), (moved.FromKey, moved.ToKey));
    }

    /// <summary>
    /// A key refused once the step has done work is passed over all the same, and no new launch is given the next one:
    /// the work goes on from what the engine stored, by its fallback.
    /// </summary>
    [Fact]
    public async Task AKeyRefusedAfterTheStepWorked_IsLeftToTheEnginesFallback()
    {
        using var kit = new Kit();
        kit.Script(new JsonObject
        {
            ["byKey"] = new JsonObject
            {
                ["sk-one"] = new JsonArray(
                    new JsonObject { ["text"] = "worked" },
                    new JsonObject { ["error"] = "authentication_failed" }),
            },
        });
        var credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("KEY_ONE", "sk-one"), new AgentKey("KEY_TWO", "sk-two")]);
        var run = kit.Run(credential: credential);
        run.Messages.Post("go on");

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(["KEY_ONE"], result.KeysRefused);
        Assert.Single(kit.Launches());
    }

    /// <summary>
    /// A session file a launch left all the same, told to keep none, is removed - its own, by the session id the client
    /// chose, and nothing else - and said.
    /// </summary>
    [Fact]
    public async Task ASessionFileTheLaunchLeft_IsRemoved_AndNothingElse()
    {
        using var kit = new Kit();
        var config = kit.Temp.Combine("claude-config");
        var other = Path.Combine(config, "projects", "proj", "someone-else.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "{}\n");
        kit.Script([new JsonObject { ["sessionFile"] = "proj" }]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(credential: new AgentCredential(AgentAuth.Login, [], ConfigDirectory: config)), sink, Token);

        var removed = Assert.Single(result.SessionFilesRemoved);
        Assert.Equal($"{result.Session}.jsonl", Path.GetFileName(removed));
        Assert.False(File.Exists(removed));
        Assert.True(File.Exists(other));
        Assert.Contains(sink.Of<Notice>(), notice => notice.Text.Contains(removed, StringComparison.Ordinal));
    }

    /// <summary>A launch whose program does not start never began: <see cref="AgentNotStartedBecause.ProgramMissing"/>.</summary>
    [Fact]
    public async Task ALaunchWhoseProgramDoesNotStart_NeverBegan()
    {
        using var kit = new Kit();
        kit.Script(new JsonArray());

        var missing = kit.Temp.Combine("no-claude.exe");

        var result = await kit.Client().RunAsync(kit.Run() with { Program = missing }, new Sink(), Token);

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ProgramMissing), (result.Status, result.NotStartedBecause));
        Assert.Equal($"client 'claude-code' was not started: Executable '{missing}' does not exist.", result.Error);
        Assert.Equal(0, result.Turns);
    }

    /// <summary>
    /// The user's settings in the launch's Claude Code directory are read before any launch, and one that would give the
    /// launch a credential - an identity name in its <c>env</c>, in any case, or a key helper - refuses the run by the
    /// file and the setting, never started and nothing launched.
    /// </summary>
    [Theory]
    [InlineData("""{"env":{"ANTHROPIC_API_KEY":"sk-user"}}""", "ANTHROPIC_API_KEY in its env")]
    [InlineData("""{"env":{"ANTHROPIC_AUTH_TOKEN":"t"}}""", "ANTHROPIC_AUTH_TOKEN in its env")]
    [InlineData("""{"env":{"CLAUDE_CODE_OAUTH_TOKEN":"t"}}""", "CLAUDE_CODE_OAUTH_TOKEN in its env")]
    [InlineData("""{"env":{"ANTHROPIC_BASE_URL":"https://elsewhere.example"}}""", "ANTHROPIC_BASE_URL in its env")]
    [InlineData("""{"env":{"anthropic_api_key":"sk-user"}}""", "anthropic_api_key in its env")]
    [InlineData("""{"apiKeyHelper":"/usr/local/bin/key"}""", "apiKeyHelper")]
    public async Task TheUsersSettings_ThatWouldGiveTheLaunchACredential_RefuseTheRun_BeforeAnyLaunch(string settings, string said)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        var file = Path.Combine(kit.ClaudeDirectory, ClaudeUserSettings.FileName);
        File.WriteAllText(file, settings);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        // The program is there and would start: what is wrong is the configuration it would read.
        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ConfigurationWrong), (result.Status, result.NotStartedBecause));
        Assert.Equal(
            $"client 'claude-code' was not started: '{file}', the user's Claude Code settings this launch reads, sets {said}, and this client signs in with the machine's login; a credential is read from the slot alone. Take it out of that file, or give the client a directory of its own in the slot: CLAUDE_CONFIG_DIR, or the name agentClients.claude-code.configDir gives",
            result.Error);
        Assert.Empty(kit.Runner.Requests);
    }

    /// <summary>
    /// User settings that do not read, or hold no JSON object, refuse the run: what they would give a launch cannot be
    /// known.
    /// </summary>
    [Theory]
    [InlineData("""{"env": {""", "does not read as JSON (")]
    [InlineData("[]", "holds no JSON object;")]
    [InlineData("1", "holds no JSON object;")]
    [InlineData("null", "holds no JSON object;")]
    public async Task TheUsersSettings_ThatDoNotRead_RefuseTheRun(string settings, string said)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        var file = Path.Combine(kit.ClaudeDirectory, ClaudeUserSettings.FileName);
        File.WriteAllText(file, settings);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ConfigurationWrong), (result.Status, result.NotStartedBecause));
        Assert.StartsWith($"client 'claude-code' was not started: '{file}', the user's Claude Code settings this launch reads, {said}", result.Error, StringComparison.Ordinal);
        Assert.Empty(kit.Runner.Requests);
    }

    /// <summary>
    /// What reached the agent beside its prompt is known: the launch's start names the user's settings file it read and
    /// its SHA-256, and says the user's hooks are off - every launch's own settings turn them off - and a launch with no
    /// user's settings names none.
    /// </summary>
    [Fact]
    public async Task TheLaunchesStart_NamesTheUsersSettingsAndTheirDigest_AndSaysTheHooksAreOff()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject(), new JsonObject()]);
        var file = Path.Combine(kit.ClaudeDirectory, ClaudeUserSettings.FileName);
        const string Settings = """{"env":{"EDITOR_THEME":"dark"},"hooks":{"PreToolUse":[{"matcher":"*","hooks":[{"type":"command","command":"echo hooked"}]}]}}""";
        File.WriteAllText(file, Settings);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var started = Assert.Single(sink.Of<SessionStarted>());
        Assert.Equal(
            (file, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))), "off"),
            (started.SettingsFile, started.SettingsDigest, started.Hooks));

        var own = JsonNode.Parse(File.ReadAllText(Path.Combine(kit.StepDirectory, ClaudeLaunch.SettingsFileName)))!;
        Assert.True(own["disableAllHooks"]!.GetValue<bool>());

        File.Delete(file);
        var none = new Sink();
        await kit.Client().RunAsync(kit.Run(), none, Token);

        var bare = Assert.Single(none.Of<SessionStarted>());
        Assert.Equal((null, null, "off"), (bare.SettingsFile, bare.SettingsDigest, bare.Hooks));
    }

    /// <summary>The settings read are the launch's Claude Code directory's: its credential's, where it names one, over the tool's own.</summary>
    [Fact]
    public async Task TheSettingsRead_AreTheCredentialsDirectorysWhereItNamesOne()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        var named = Directory.CreateDirectory(kit.Temp.Combine("login-b")).FullName;
        File.WriteAllText(Path.Combine(kit.ClaudeDirectory, ClaudeUserSettings.FileName), """{"apiKeyHelper":"/bin/key"}""");
        File.WriteAllText(Path.Combine(named, ClaudeUserSettings.FileName), "{}");

        var clean = await kit.Client().RunAsync(kit.Run(credential: new AgentCredential(AgentAuth.Login, [], ConfigDirectory: named)), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Completed, clean.Status);

        File.WriteAllText(Path.Combine(named, ClaudeUserSettings.FileName), """{"env":{"ANTHROPIC_API_KEY":"sk"}}""");
        File.WriteAllText(Path.Combine(kit.ClaudeDirectory, ClaudeUserSettings.FileName), "{}");

        var refused = await kit.Client().RunAsync(kit.Run(credential: new AgentCredential(AgentAuth.Login, [], ConfigDirectory: named)), new Sink(), Token);

        Assert.Equal(AgentRunStatus.NotStarted, refused.Status);
        Assert.Contains($"'{Path.Combine(named, ClaudeUserSettings.FileName)}'", refused.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A launch that does not answer the host's <c>initialize</c> request within its bound is not a child the engine is the
    /// host of: it is ended at once - a launch that would not end by itself included - and the step never started, naming
    /// the program's version.
    /// </summary>
    [Fact]
    public async Task ALaunchThatDoesNotAnswerInitialize_NeverStarted_AndIsEndedAtOnce()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["noInitialize"] = true, ["ignoreEof"] = true }]);
        kit.Clock = new BoundsClock((ClaudeCodeClient.InitializeBound, TimeSpan.FromSeconds(1)), (ClaudeCodeClient.EndBound, TimeSpan.FromMinutes(10)));

        var result = await kit.Client().RunAsync(kit.Run() with { ProgramVersion = "2.1.287" }, new Sink(), Token);

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ProgramMissing), (result.Status, result.NotStartedBecause));
        Assert.Equal(
            "client 'claude-code' was not started: claude 2.1.287 did not answer the host's initialize request within 30 s: this tool runs Claude Code only as its host",
            result.Error);
    }

    /// <summary>A launch that refuses the host's <c>initialize</c> request never started, saying what it answered.</summary>
    [Fact]
    public async Task ALaunchThatRefusesInitialize_NeverStarted()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["initializeError"] = true }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(AgentRunStatus.NotStarted, result.Status);
        Assert.Equal(
            "client 'claude-code' was not started: claude refused the host's initialize request (no host is taken here): this tool runs Claude Code only as its host",
            result.Error);
    }

    /// <summary>
    /// A launch whose init offers a tool the launch was not given ends never started, naming the tool: one this tool never
    /// gives an agent as that, any other as one the launch was not given - the answer's own tool among them where the
    /// step names no output.
    /// </summary>
    [Theory]
    [InlineData("Agent", "which this tool never gives an agent")]
    [InlineData("AskUserQuestion", "which this tool never gives an agent")]
    [InlineData("WebSearch", "which this launch was not given")]
    [InlineData("StructuredOutput", "which this launch was not given")]
    public async Task AnInitOfferingAToolTheLaunchWasNotGiven_EndsItNeverStarted(string tool, string why)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["initTools"] = new JsonArray("Read", tool) }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ProgramMissing), (result.Status, result.NotStartedBecause));
        Assert.Equal($"client 'claude-code' was not started: claude fake offered the tool '{tool}', {why}", result.Error);
    }

    /// <summary>
    /// The credential the init names is held to the launch's way of signing in, by what each gives there; another ends
    /// the launch never started, naming both.
    /// </summary>
    [Theory]
    [InlineData(AgentAuth.Login, "ANTHROPIC_API_KEY", "the machine's login, which Claude Code names 'none' or '/login managed key'")]
    [InlineData(AgentAuth.Login, "apiKeyHelper", "the machine's login, which Claude Code names 'none' or '/login managed key'")]
    [InlineData(AgentAuth.ApiKey, "none", "a key of its provider's apiKeyNames, which Claude Code names 'ANTHROPIC_API_KEY'")]
    [InlineData(AgentAuth.OAuthToken, "ANTHROPIC_API_KEY", "the token of auth: oauthToken, which Claude Code names 'none'")]
    [InlineData(AgentAuth.AuthToken, "/login managed key", "the token of auth: authToken, which Claude Code names 'none'")]
    public async Task AnInitWhoseCredentialIsNotTheLaunchs_EndsItNeverStarted_NamingBoth(string auth, string source, string given)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["apiKeySource"] = source }]);
        var credential = auth == AgentAuth.Login ? null : new AgentCredential(auth, [new AgentKey("GIVEN", "v-1")]);

        var result = await kit.Client().RunAsync(kit.Run(credential: credential), new Sink(), Token);

        Assert.Equal(AgentRunStatus.NotStarted, result.Status);
        Assert.Equal($"client 'claude-code' was not started: claude fake signed in by '{source}', and this launch signs in with {given}", result.Error);
    }

    /// <summary>Each way of signing in takes the credential Claude Code names for it.</summary>
    [Theory]
    [InlineData(AgentAuth.Login, "none")]
    [InlineData(AgentAuth.Login, "/login managed key")]
    [InlineData(AgentAuth.ApiKey, "ANTHROPIC_API_KEY")]
    [InlineData(AgentAuth.OAuthToken, "none")]
    public async Task AnInitWhoseCredentialIsTheLaunchs_Runs(string auth, string source)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["apiKeySource"] = source }]);
        var credential = auth == AgentAuth.Login ? null : new AgentCredential(auth, [new AgentKey("GIVEN", "v-1")]);

        var result = await kit.Client().RunAsync(kit.Run(credential: credential), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
    }

    /// <summary>
    /// A call the engine was never asked about that ran - whether it succeeded or failed - fails the step by the tool's
    /// name, its result told, and the launch is ended at once, one that would not end by itself included.
    /// </summary>
    [Theory]
    [InlineData("unasked")]
    [InlineData("unaskedFailed")]
    [InlineData("unaskedFailedMarked")]
    [InlineData("unaskedFailedBare")]
    public async Task ACallRunUnasked_FailsTheStepByName_AndEndsTheLaunchAtOnce(string how)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" }, [how] = true }), ["ignoreEof"] = true }]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(), sink, Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal("Claude Code ran 'Read' without asking the engine (claude fake)", result.Error);
        Assert.Equal(how == "unaskedFailedMarked" ? "<tool_use_error>ran unasked</tool_use_error>" : "ran unasked", Assert.Single(sink.Of<ToolCallFinished>()).Result);
    }

    /// <summary>
    /// A call the handoff's interrupt cut while it was asked about - its ask withdrawn, its result Claude Code's own, stamped
    /// as not run - is no call run, though the engine refused it: the agent is asked for its handoff, and its answer is the
    /// run's.
    /// </summary>
    [Fact]
    public async Task ACallTheInterruptCutWhileItWasAsked_IsNoCallRun_AndTheHandoffIsAsked()
    {
        using var kit = new Kit();
        var outside = kit.Temp.Combine("elsewhere", "x.txt");
        kit.Script(
        [
            new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Write", ["input"] = new JsonObject { ["file_path"] = outside, ["content"] = "x" }, ["cutWhileAsked"] = true }) },
            new JsonObject { ["text"] = "Nothing done yet." },
        ]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is ToolCallStarted)
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.True(result.Status == AgentRunStatus.HandedOff, $"{result.Status}: {result.Error}");
        Assert.Equal("Nothing done yet.", result.Handoff?.Answer);
    }

    /// <summary>A call that failed on its input before anything was asked never ran: it is no call run unasked.</summary>
    [Fact]
    public async Task ACallThatFailedOnItsInput_BeforeAnythingWasAsked_IsNoCallRunUnasked()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["path"] = "a.txt" }, ["invalidInput"] = true }) }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
    }

    /// <summary>
    /// Where the engine cannot decide, it never allows, and the launch goes on: a request it answers no kind of is answered
    /// <c>error</c>; a call its own deciding fails on is denied by the rule <c>undecided</c>, saying the fault, and recorded
    /// as every denial is - the model reads why, the events, <c>denials.jsonl</c> and the result count it - so no fault of
    /// the engine's escapes unrecorded. Both answers are kept with the control messages.
    /// </summary>
    [Fact]
    public async Task WhereTheEngineCannotDecide_TheCallIsDeniedAndRecorded_AndARequestItAnswersNoKindOfIsAnError()
    {
        using var kit = new Kit();
        kit.Script(
        [
            new JsonObject
            {
                ["controlRequest"] = new JsonObject { ["subtype"] = "hook_callback", ["callback_id"] = "hook_0" },
                ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "faulty.txt" } }),
            },
        ]);
        kit.FileSystem = new FaultsOn(kit.Harness.FileSystem, "faulty.txt");
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.False(Assert.Single(sink.Of<ToolCallFinished>()).Ok);
        Assert.Equal(1, result.Denials);

        var faulty = Path.Combine(kit.Worktree, "faulty.txt");
        var said = $"the engine could not decide this call ('{faulty}' cannot be looked at): it denies what it cannot decide";
        var answers = Assert.Single(kit.Launches()).Read
            .Where(line => line["type"]?.GetValue<string>() == "control_response")
            .Select(line => line["response"]!)
            .ToList();
        Assert.Equal(2, answers.Count);
        Assert.Equal(("error", "this host answers no 'hook_callback' request"), (answers[0]["subtype"]!.GetValue<string>(), answers[0]["error"]!.GetValue<string>()));
        Assert.Equal(("deny", said), (answers[1]["response"]!["behavior"]!.GetValue<string>(), answers[1]["response"]!["message"]!.GetValue<string>()));

        var denied = Assert.Single(sink.Of<PermissionDenied>());
        Assert.Equal(("Read", "faulty.txt", AgentPolicy.UndecidedRule, AgentPermissionSource.Builtin, said), (denied.Tool, denied.Subject, denied.Rule, denied.Source, denied.Reason));

        var recorded = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(kit.StepDirectory, ClaudeLaunch.DenialsFileName))))!;
        Assert.Equal(("Read", "undecided", "Builtin", said), (recorded["tool"]!.GetValue<string>(), recorded["rule"]!.GetValue<string>(), recorded["source"]!.GetValue<string>(), recorded["reason"]!.GetValue<string>()));
        Assert.Equal(1, File.ReadAllLines(Path.Combine(kit.StepDirectory, ClaudeLaunch.ControlFileName)).Count(line => line.Contains("\"subtype\":\"error\"", StringComparison.Ordinal)));
    }

    /// <summary>A call that ran though the engine refused it fails the step by the tool's name.</summary>
    [Fact]
    public async Task ACallRunThoughTheEngineRefusedIt_FailsTheStep()
    {
        using var kit = new Kit();
        var outside = kit.Temp.Combine("elsewhere", "x.txt");
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Write", ["input"] = new JsonObject { ["file_path"] = outside, ["content"] = "x" }, ["ranThoughDenied"] = true }), ["ignoreEof"] = true }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal("Claude Code ran 'Write' though the engine refused it (claude fake)", result.Error);
    }

    /// <summary>
    /// A call Claude Code refused by itself, unasked, that the engine allows was refused by a launch that does not ask its
    /// host - as one started without <c>--permission-prompt-tool stdio</c> - and ends the launch never started, nothing
    /// of it having run; one the engine refuses too is its own deny rules' second layer, a denial like any other.
    /// </summary>
    [Fact]
    public async Task ACallClaudeCodeRefusedByItself_ThatTheEngineAllows_EndsTheLaunch_AndOneItRefusesIsADenial()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" }, ["refusedByClaude"] = true }) }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ProgramMissing), (result.Status, result.NotStartedBecause));
        Assert.Equal(
            "client 'claude-code' was not started: Claude Code refused 'Read' without asking the engine, which allows it (claude fake): Claude requested permissions to use this tool, but you haven't granted it yet.; this tool runs Claude Code only as its host",
            result.Error);

        var outside = kit.Temp.Combine("elsewhere", "x.txt");
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Write", ["input"] = new JsonObject { ["file_path"] = outside, ["content"] = "x" }, ["refusedByClaude"] = true }) }]);
        var sink = new Sink();

        var layered = await kit.Client().RunAsync(kit.Run(), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, layered.Status);
        Assert.Equal(1, layered.Denials);
        Assert.Equal(AgentPermissionSource.ClaudeCode, Assert.Single(sink.Of<PermissionDenied>()).Source);
    }

    /// <summary>
    /// A launch found not to be the engine's child once something it did stands - a call that ran, a turn that ended -
    /// fails the step rather than never starting it: what it did is there.
    /// </summary>
    [Theory]
    [InlineData("aCallRan")]
    [InlineData("aTurnEnded")]
    public async Task ALaunchFoundNotTheEnginesChild_OnceSomethingItDidStands_FailsTheStep(string after)
    {
        using var kit = new Kit();
        var refused = new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" }, ["refusedByClaude"] = true };
        kit.Script(after == "aCallRan"
            ? [new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" } }, refused) }]
            : [new JsonObject { ["answer"] = new JsonObject { ["answers"] = "a" } }, new JsonObject { ["calls"] = new JsonArray(refused) }]);

        var result = await kit.Client().RunAsync(kit.Run(new AgentOutput("o", Schema)), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.StartsWith("Claude Code refused 'Read' without asking the engine, which allows it (claude fake)", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A request the model declined ends the step <see cref="AgentRunStatus.Refused"/>, with what it was of where the
    /// provider names it - read wherever the launch says it: the stream's stop, the message, the turn's end.
    /// </summary>
    [Theory]
    [InlineData("cyber", null, "cyber")]
    [InlineData("", null, null)]
    [InlineData("cyber", "stream", "cyber")]
    [InlineData("cyber", "message", "cyber")]
    [InlineData("cyber", "result", null)]
    public async Task ADeclinedRequest_EndsTheStepRefused_WithWhatItWasOf(string category, string? by, string? declinedFor)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["declined"] = category, ["declinedBy"] = by }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Refused, result.Status);
        Assert.Equal(declinedFor, result.DeclinedFor);
        Assert.StartsWith(declinedFor is null ? "the model declined the request: " : $"the model declined the request ({declinedFor}): ", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Limit);
    }

    /// <summary>
    /// A rate limit event that says <c>rejected</c> is the account at its limit and ends the step
    /// <see cref="AgentRunStatus.RateLimited"/> at once, with when the limit lifts; <c>credits_required</c> is its included
    /// usage spent. One that only warns ends nothing.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("credits_required", true)]
    public async Task ARejectedRateLimit_EndsTheStepRateLimited_AtOnce(string? code, bool spent)
    {
        using var kit = new Kit();
        var info = new JsonObject { ["status"] = "rejected", ["resetsAt"] = 1_790_000_000L, ["rateLimitType"] = "five_hour" };

        if (code is not null)
        {
            info["errorCode"] = code;
        }

        kit.Script([new JsonObject { ["rateLimit"] = info, ["hang"] = true }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(AgentRunStatus.RateLimited, result.Status);
        Assert.Equal(new AgentAccountLimit(spent, DateTimeOffset.FromUnixTimeSeconds(1_790_000_000)), result.Limit);
        Assert.Contains("five_hour", result.Error, StringComparison.Ordinal);

        kit.Script([new JsonObject { ["rateLimit"] = new JsonObject { ["status"] = "allowed_warning", ["rateLimitType"] = "seven_day" } }]);

        var warned = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Completed, warned.Status);
        Assert.Null(warned.Limit);
    }

    /// <summary>
    /// A turn Claude Code ends on its account's limit after its own tries ends the step
    /// <see cref="AgentRunStatus.RateLimited"/>: a rate limit with when it lifts where an event said it, a billing error a
    /// spent balance. An overloaded service is no limit of the account's.
    /// </summary>
    [Theory]
    [InlineData("rate_limit", AgentRunStatus.RateLimited, false)]
    [InlineData("billing_error", AgentRunStatus.RateLimited, true)]
    [InlineData("overloaded", AgentRunStatus.Failed, null)]
    public async Task ATurnEndedOnTheAccountsLimit_EndsTheStepRateLimited(string error, AgentRunStatus status, bool? spent)
    {
        using var kit = new Kit();
        var lifts = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        kit.Script([new JsonObject { ["rateLimit"] = new JsonObject { ["status"] = "allowed_warning", ["resetsAt"] = lifts.ToUnixTimeSeconds() }, ["error"] = error, ["text"] = "API Error" }]);

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(status, result.Status);
        Assert.Equal(spent is { } balance ? new AgentAccountLimit(balance, balance ? null : lifts) : null, result.Limit);
    }

    /// <summary>
    /// A message the launch never takes stalls the step once the stall bound passes, and the launch is ended at once; a
    /// step with no stall bound waits as long as its other bounds let it.
    /// </summary>
    [Fact]
    public async Task AMessageTheLaunchNeverTakes_StallsTheStep_AndTheLaunchIsEndedAtOnce()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["untaken"] = true, ["ignoreEof"] = true }]);
        var run = kit.Run() with { Limits = new AgentLimits(30, 2.5m, null, StallSeconds: 1) };

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.Equal(AgentRunStatus.Stalled, result.Status);
        Assert.Equal("Claude Code did not take the engine's message within 1 s (claude)", result.Error);
    }

    /// <summary>
    /// A launch whose every message is taken within the stall bound is never stalled, however long it runs: a message
    /// given while a turn runs is bounded from the turn's end, and a bound passed for a message already taken ends
    /// nothing.
    /// </summary>
    [Fact]
    public async Task ALaunchWhoseMessagesAreTakenInTime_StallsNothing_HoweverLongItRuns()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["slowMs"] = 3500, ["text"] = "first" }, new JsonObject { ["slowMs"] = 1500, ["text"] = "second" }]);
        var run = kit.Run() with { Limits = new AgentLimits(30, 2.5m, null, StallSeconds: 2) };
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted { Turn: 1 })
            {
                run.Messages.Post("Also say second.");
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Error}");
        Assert.Equal(("second", 2), (result.FinalText, result.Turns));
    }

    /// <summary>
    /// A bound started for a message the launch has since taken ends nothing, though another message waits when it passes:
    /// that message's bound is its own, from when it was written.
    /// </summary>
    [Fact]
    public async Task ABoundForAMessageTaken_EndsNothing_ThoughAnotherWaits()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["slowMs"] = 4000, ["answer"] = new JsonObject { ["answers"] = "a" } }, new JsonObject { ["takeAfterMs"] = 5000 }]);
        var run = kit.Run(new AgentOutput("o", Schema)) with { Limits = new AgentLimits(30, 2.5m, null, StallSeconds: 7) };

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Error}");
        Assert.Equal(2, result.Turns);
    }

    /// <summary>
    /// A message written while a call is asked is taken into the turn under way, as Claude Code takes it: that turn's result
    /// answers both, and is the step's answer.
    /// </summary>
    [Fact]
    public async Task AMessageTakenIntoTheTurnUnderWay_IsAnsweredByThatTurnsResult()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" } }), ["waitForMessage"] = true, ["text"] = "Read it, and second." }]);
        var run = kit.Run() with { Limits = new AgentLimits(30, 2.5m, null, StallSeconds: 2) };
        var sink = new Sink(@event =>
        {
            if (@event is ToolCallFinished)
            {
                run.Messages.Post("Also say second.");
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Error}");
        Assert.Equal(("Read it, and second.", 1), (result.FinalText, result.Turns));
    }

    /// <summary>
    /// A message written before the launch took its prompt is a turn of its own: the prompt's result is not the step's
    /// answer, the message's is.
    /// </summary>
    [Fact]
    public async Task AMessageWrittenBeforeThePromptWasTaken_IsAnsweredByItsOwnTurn()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["text"] = "first" }, new JsonObject { ["text"] = "second" }]);
        var run = kit.Run();
        run.Messages.Post("Also say second.");

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Error}");
        Assert.Equal(("second", 2), (result.FinalText, result.Turns));
    }

    /// <summary>
    /// The stall bound starts once the launch answered the initialize: a launch slow to start is not stalled for the time
    /// it took.
    /// </summary>
    [Fact]
    public async Task TheStallBound_StartsOnceTheLaunchAnsweredItsInitialize()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["initializeAfterMs"] = 2500 }]);
        var run = kit.Run() with { Limits = new AgentLimits(30, 2.5m, null, StallSeconds: 2) };

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Error}");
    }

    /// <summary>
    /// A message given while a turn runs that the launch never takes stalls the step once the bound passes after that
    /// turn's end - not before it, however long the turn runs.
    /// </summary>
    [Fact]
    public async Task AMessageGivenWhileATurnRuns_ThatIsNeverTaken_StallsTheStepAfterTheTurnsEnd()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["slowMs"] = 2500 }, new JsonObject { ["untaken"] = true, ["ignoreEof"] = true }]);
        var run = kit.Run() with { Limits = new AgentLimits(30, 2.5m, null, StallSeconds: 1) };
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted { Turn: 1 })
            {
                run.Messages.Post("Also say second.");
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.Equal(AgentRunStatus.Stalled, result.Status);
        Assert.IsType<TurnFinished>(sink.Events.Last(@event => @event is TurnFinished or MessageSent));
    }

    /// <summary>
    /// A handoff asked before the launch took its prompt asks the agent at once: the interrupt drops the prompt, no turn's
    /// end is waited for, and the agent's answer is the handoff.
    /// </summary>
    [Fact]
    public async Task AHandoffAskedBeforeTheLaunchTookItsPrompt_AsksTheAgentAtOnce()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["untaken"] = true }, new JsonObject { ["text"] = "Nothing done yet." }]);
        var run = kit.Run();
        run.Handoff.Ask();
        var sink = new Sink();

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.Equal(AgentRunStatus.HandedOff, result.Status);
        Assert.Equal("Nothing done yet.", result.Handoff?.Answer);
        Assert.Equal(1, result.Turns);
    }

    /// <summary>
    /// A message Claude Code had taken for its turn as the interrupt came - named in neither of the receipt's lists - is
    /// waited for: the agent is asked once that turn, cut, ends, and the answer is the question's turn, not the late one's.
    /// </summary>
    [Fact]
    public async Task AMessageTheInterruptDidNotDrop_IsWaitedFor_AndTheAnswerIsTheQuestions()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["takenLate"] = true }, new JsonObject { ["text"] = "Nothing done yet." }]);
        var run = kit.Run();
        run.Handoff.Ask();

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.True(result.Status == AgentRunStatus.HandedOff, $"{result.Status}: {result.Error}");
        Assert.Equal("Nothing done yet.", result.Handoff?.Answer);
        Assert.Equal(2, result.Turns);
    }

    /// <summary>
    /// A message taken late is waited for though the receipt came while a turn ran, and only the messages the receipt
    /// names are dropped - an id it names that the engine never gave is no message of the engine's: the turn the interrupt
    /// cut ends, the late turn begins and is cut, and only then is the agent asked.
    /// </summary>
    [Fact]
    public async Task AMessageTakenLate_IsWaitedFor_ThoughTheReceiptCameWhileATurnRan()
    {
        using var kit = new Kit();

        // The third message is dropped, so the question is the third entry's.
        kit.Script(
        [
            new JsonObject { ["hang"] = true, ["receiptAlsoNames"] = new JsonArray("a-message-no-host-wrote") },
            new JsonObject { ["takenLate"] = true },
            new JsonObject { ["text"] = "Nothing done yet." },
        ]);
        var run = kit.Run();
        var posted = 0;
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted { Turn: 1 })
            {
                run.Messages.Post("Also say second.");
                run.Messages.Post("Also say third.");
            }
            else if (@event is MessageSent { Which: AgentMessageKind.PlanUpdate } && ++posted == 2)
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.True(result.Status == AgentRunStatus.HandedOff, $"{result.Status}: {result.Error}");
        Assert.Equal("Nothing done yet.", result.Handoff?.Answer);
        Assert.Equal(3, result.Turns);
    }

    /// <summary>
    /// An interrupt written before the launch answered its initialize request is bounded from that answer: a launch slow
    /// to start is not taken for one that does not take the interrupt.
    /// </summary>
    [Fact]
    public async Task AnInterruptWrittenBeforeTheLaunchAnswered_IsBoundedFromItsAnswer()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["initializeAfterMs"] = 2500, ["untaken"] = true }, new JsonObject { ["text"] = "Nothing done yet." }]);
        kit.Clock = new BoundsClock((ClaudeCodeClient.EndBound, TimeSpan.FromMinutes(10)), (ClaudeCodeClient.InterruptBound, TimeSpan.FromSeconds(1)));
        var run = kit.Run();
        run.Handoff.Ask();

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.True(result.Status == AgentRunStatus.HandedOff, $"{result.Status}: {result.Error}");
        Assert.Equal("Nothing done yet.", result.Handoff?.Answer);
    }

    /// <summary>
    /// An interrupt written before the launch answered its initialize request, and never taken, ends the handoff once its
    /// own bound passes from that answer - not only when the handoff's bound does.
    /// </summary>
    [Fact]
    public async Task AnInterruptWrittenBeforeTheLaunchAnswered_AndNeverTaken_EndsAtItsOwnBound()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["initializeAfterMs"] = 500, ["untaken"] = true, ["ignoreInterrupt"] = true }]);
        kit.Clock = new BoundsClock((ClaudeCodeClient.EndBound, TimeSpan.FromMinutes(10)), (ClaudeCodeClient.InterruptBound, TimeSpan.FromSeconds(1)));
        var run = kit.Run();
        run.Handoff.Ask();

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.True(result.Status == AgentRunStatus.HandedOff, $"{result.Status}: {result.Error}");
        Assert.Equal($"the agent did not take the interrupt within {ClaudeCodeClient.InterruptBound.TotalSeconds:0} s", result.Handoff?.WhyNone);
    }

    /// <summary>A name of Claude Code's that says only where a program is - Git's bash - reaches the launch from the tool's environment.</summary>
    [Fact]
    public async Task TheGitBashPath_ReachesTheLaunch()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        kit.Own["CLAUDE_CODE_GIT_BASH_PATH"] = @"C:\Program Files\Git\bin\bash.exe";
        kit.Own["CLAUDE_CODE_ENTRYPOINT"] = "claude-desktop";

        await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        var seen = Assert.Single(kit.Launches()).Environment;
        Assert.Equal(@"C:\Program Files\Git\bin\bash.exe", seen["CLAUDE_CODE_GIT_BASH_PATH"]);
        Assert.False(seen.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
    }

    /// <summary>A settings file an editor saved with a byte-order mark reads as its JSON: what it would give a launch is refused all the same.</summary>
    [Fact]
    public async Task TheUsersSettings_WithAByteOrderMark_ReadAsTheirJson()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        File.WriteAllText(Path.Combine(kit.ClaudeDirectory, ClaudeUserSettings.FileName), """{"env":{"ANTHROPIC_API_KEY":"sk"}}""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        Assert.Equal(AgentRunStatus.NotStarted, result.Status);
        Assert.Contains("sets ANTHROPIC_API_KEY in its env", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a turn the handoff cut said of itself - a decline - is that turn's: the handoff turn after it answers, and the
    /// run ends <see cref="AgentRunStatus.HandedOff"/> with the answer.
    /// </summary>
    [Fact]
    public async Task ADeclineInTheTurnAHandoffCut_IsThatTurns_AndTheHandoffTurnAnswers()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["declined"] = "cyber", ["hang"] = true }, new JsonObject { ["text"] = "Stopped before any change." }]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.Equal(AgentRunStatus.HandedOff, result.Status);
        Assert.Equal("Stopped before any change.", result.Handoff?.Answer);
    }

    /// <summary>
    /// A launch that does what nothing decided in the handoff turn fails the step, and the handoff it did not give is
    /// said: every route out of a handoff says whether an answer came.
    /// </summary>
    [Fact]
    public async Task ACallRunUnasked_InTheHandoffTurn_FailsTheStep_AndSaysNoHandoffCame()
    {
        using var kit = new Kit();
        kit.Script(
        [
            new JsonObject { ["hang"] = true },
            new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" }, ["unasked"] = true }) },
        ]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(new HandoffAnswered(false, result.Error), Assert.Single(sink.Of<HandoffAnswered>()));
    }

    /// <summary>
    /// A hard stop while a turn runs ends the launch whole - one that would not end by itself included - and comes back as
    /// a stop, well inside the bound a launch is given to end in by itself: nothing of the launch is waited on.
    /// </summary>
    [Fact]
    public async Task AStopMidTurn_EndsTheLaunchWhole_AndComesBackAsAStop()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true, ["ignoreEof"] = true }]);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                _ = stop.CancelAsync();
            }
        });

        // The kit's launches are given ten minutes to end by themselves: one that came back in a minute was ended whole.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kit.Client().RunAsync(kit.Run(), sink, stop.Token).WaitAsync(TimeSpan.FromSeconds(60), Token));

        Assert.Single(sink.Of<TurnStarted>());
        Assert.Empty(sink.Of<TurnFinished>());
    }

    /// <summary>
    /// A hard stop while a turn runs, through the runner, is recorded as the run stopped: its stream's end and
    /// <c>result.json</c> say <see cref="AgentRunStatus.Cancelled"/> and <see cref="AgentRunner.StoppedError"/>, whatever
    /// the launch's own end raised.
    /// </summary>
    [Fact]
    public async Task AStopMidTurn_ThroughTheRunner_IsRecordedAsTheRunStopped()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true, ["ignoreEof"] = true }]);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                _ = stop.CancelAsync();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kit.StepRunner().RunAsync(kit.Step(), sink, stop.Token).WaitAsync(TimeSpan.FromSeconds(60), Token));

        var result = JsonSerializer.Deserialize<AgentRunResult>(File.ReadAllText(Path.Combine(kit.StepDirectory, AgentRunner.ResultFileName)), JsonStateFile.Options)!;
        Assert.Equal((AgentRunStatus.Cancelled, AgentRunner.StoppedError), (result.Status, result.Error));
        Assert.Null(result.Problem());
        Assert.Equal(new RunFinished(AgentRunStatus.Cancelled, AgentRunner.StoppedError) { At = sink.Events[^1].At, Seq = sink.Events[^1].Seq }, sink.Events[^1]);
    }

    /// <summary>
    /// A launch is contained and records its session under its step while it runs - on Linux and macOS, where a session
    /// would outlive the engine that started it - so a killed engine's launch is ended by the next command; the record is
    /// taken back once the launch has ended.
    /// </summary>
    [Fact]
    public async Task ALaunch_IsContained_AndRecordsItsSessionUnderItsStep()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a contained child there is held in a job that ends with its starter and nothing is recorded, which ChildSessionsTests.AContainedChild_IsRecordedOnlyWhereItsSessionWouldOutliveItsStarter holds; that a launch asks to be contained with its record under its step, ALaunchsRequest_IsContained_WithItsSessionRecordedUnderItsStep holds everywhere.");

        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true }, new JsonObject { ["text"] = "Nothing done yet." }]);
        var run = kit.Run();
        var children = OrchestratorLayout.ChildrenDirectory(kit.StepDirectory);
        string[] whileItRan = [];
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted { Turn: 1 })
            {
                whileItRan = Directory.GetFiles(children);
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token);

        Assert.Equal(AgentRunStatus.HandedOff, result.Status);

        var record = JsonNode.Parse(File.ReadAllText(Assert.Single(whileItRan)))!;
        var session = record["session"]!.GetValue<int>();
        Assert.Equal(OrchestratorLayout.ChildFile(kit.StepDirectory, session), whileItRan[0]);
        Assert.Equal((TestHost.AssemblyProgram, Environment.ProcessId), (record["program"]!.GetValue<string>(), record["owner"]!["processId"]!.GetValue<int>()));
        Assert.Empty(Directory.GetFiles(children));
    }

    /// <summary>
    /// A launch asks, on every system, to be contained with its session recorded under its own step: the record its request
    /// carries, once placed, is the step's own, and is taken back at the launch's end. Where the runner writes one - Linux
    /// and macOS, not Windows - ALaunch_IsContained_AndRecordsItsSessionUnderItsStep holds it for a launch that runs.
    /// </summary>
    [Fact]
    public async Task ALaunchsRequest_IsContained_WithItsSessionRecordedUnderItsStep()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);

        await kit.Client().RunAsync(kit.Run(), new Sink(), Token);

        var request = Assert.Single(kit.Runner.Requests);
        Assert.True(request.Contained, "the launch was not asked to be contained");

        var children = OrchestratorLayout.ChildrenDirectory(kit.StepDirectory);
        var record = request.SessionRecord!;
        record.Placed(Environment.ProcessId, started: null, TestHost.AssemblyProgram, group: false);

        Assert.Equal([OrchestratorLayout.ChildFile(kit.StepDirectory, Environment.ProcessId)], Directory.GetFiles(children));

        record.Ended();

        Assert.Empty(Directory.GetFiles(children));
    }

    /// <summary>
    /// A launch that does not end once its input is closed after its answer is ended whole when the bound it is given to
    /// end in passes, and the step keeps its answer: what the launch did is done, and only its end was waited on.
    /// </summary>
    [Fact]
    public async Task ALaunchThatOutlivesItsAnswer_IsEndedWholeAfterTheEndBound_AndTheStepKeepsItsAnswer()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["ignoreEof"] = true }]);
        kit.Clock = new BoundsClock((ClaudeCodeClient.EndBound, TimeSpan.FromSeconds(1)));

        var result = await kit.Client().RunAsync(kit.Run(), new Sink(), Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
    }

    /// <summary>
    /// What the step says of what its agent may do and what it signs in with reaches the launch through the runner: the
    /// tools of its capabilities and no other - a call of another is denied by name - the key its credential gives under
    /// the name Claude Code reads, and none of the names it withholds, whatever the tool's own environment holds.
    /// </summary>
    [Fact]
    public async Task TheStepsCapabilitiesCredentialAndWithheldNames_ReachTheLaunch()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Bash", ["input"] = new JsonObject { ["command"] = "echo hi" } }) }]);
        kit.Own["ZAI_API_KEY"] = "zk-ambient";
        var step = kit.Step() with
        {
            Ground = kit.Ground() with { Capabilities = [AgentCapability.Read] },
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-work")]),
            Withheld = ["ZAI_API_KEY", "WORK_KEY"],
        };
        var sink = new Sink();

        var result = await kit.StepRunner().RunAsync(step, sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var launch = Assert.Single(kit.Launches());
        Assert.Equal("Read,Grep,Glob", Value(launch.Arguments, "--tools"));

        var denied = Assert.Single(sink.Of<PermissionDenied>());
        Assert.Equal(
            ("Bash", ClaudeToolDecider.ToolsRule, "'Bash' is no tool this agent was given; it was given: Read, Grep, Glob"),
            (denied.Tool, denied.Rule, denied.Reason));

        Assert.Equal("sk-work", launch.Environment["ANTHROPIC_API_KEY"]);
        Assert.False(launch.Environment.ContainsKey("ZAI_API_KEY"), "a withheld name reached the launch");
        Assert.False(launch.Environment.ContainsKey("WORK_KEY"), "a withheld name reached the launch");
    }

    /// <summary>
    /// A key's value reaches no record of the step. With nothing to mask, the step's records name the key and never hold
    /// its value; with the step's mask, what the launch echoes of it - on its standard error, in a call's input, in its
    /// answer, and in the error its exit leaves - lands masked in every record: <c>claude-stderr.log</c>,
    /// <c>claude-control.jsonl</c>, <c>stream.jsonl</c> and the result.
    /// </summary>
    [Fact]
    public async Task AKeysValue_ReachesNoRecordOfTheStep()
    {
        const string Key = "sk-ant-test-0123456789abcdef";
        var credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", Key)]);

        using (var bare = new Kit())
        {
            bare.Script([new JsonObject { ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" } }) }]);

            var result = await bare.StepRunner().RunAsync(bare.Step() with { Credential = credential, Withheld = ["WORK_KEY"] }, null, Token);

            Assert.Equal(AgentRunStatus.Completed, result.Status);
            Assert.Equal(Key, Assert.Single(bare.Launches()).Environment["ANTHROPIC_API_KEY"]);
            Assert.Contains("WORK_KEY", File.ReadAllText(Path.Combine(bare.StepDirectory, StoredStream.FileName)), StringComparison.Ordinal);
            Assert.Empty(Holding(bare.StepDirectory, Key));
        }

        using var kit = new Kit();
        kit.Script(
        [
            new JsonObject
            {
                ["stderr"] = $"warning: signing in with {Key}",
                ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = $"{Key}.txt" } }),
                ["text"] = $"The key is {Key}.",
                ["exitAfter"] = 5,
            },
        ]);
        var mask = new ActionValues(new Dictionary<string, string>(), new Dictionary<string, string> { ["WORK_KEY"] = Key });

        var echoed = await kit.StepRunner().RunAsync(kit.Step() with { Mask = mask, Credential = credential, Withheld = ["WORK_KEY"] }, null, Token);

        Assert.Equal(AgentRunStatus.Failed, echoed.Status);
        Assert.Equal($"Claude Code ended before its turn's result, exit 5: warning: signing in with {ActionValues.Mask}", echoed.Error);

        foreach (var record in new[] { ClaudeLaunch.ErrorFileName, ClaudeLaunch.ControlFileName, StoredStream.FileName, AgentRunner.ResultFileName })
        {
            Assert.Contains(ActionValues.Mask, File.ReadAllText(Path.Combine(kit.StepDirectory, record)), StringComparison.Ordinal);
        }

        Assert.Contains($"The key is {ActionValues.Mask}.", File.ReadAllText(Path.Combine(kit.StepDirectory, StoredStream.FileName)), StringComparison.Ordinal);
        Assert.Contains($"{ActionValues.Mask}.txt", File.ReadAllText(Path.Combine(kit.StepDirectory, ClaudeLaunch.ControlFileName)), StringComparison.Ordinal);
        Assert.Empty(Holding(kit.StepDirectory, Key));
    }

    /// <summary>
    /// A handoff the agent never answers ends the run <see cref="AgentRunStatus.HandedOff"/> once the handoff's bound
    /// passes, saying so: a pause is never turned into a wait on an agent that does not answer.
    /// </summary>
    [Fact]
    public async Task AHandoffNeverAnswered_EndsHandedOffAtTheHandoffsBound()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true }, new JsonObject { ["hang"] = true }]);
        var run = kit.Run() with { Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(3)) };
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted { Turn: 1 })
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        const string Why = "the bound passed before the agent answered";
        Assert.Equal((AgentRunStatus.HandedOff, new AgentHandoff(WhyNone: Why)), (result.Status, result.Handoff));
        Assert.Equal(new HandoffAnswered(false, Why), Assert.Single(sink.Of<HandoffAnswered>()));
        Assert.Single(sink.Of<MessageSent>(), message => message.Which == AgentMessageKind.HandoffQuestion);
    }

    /// <summary>
    /// An interrupt the launch refuses ends the run <see cref="AgentRunStatus.HandedOff"/> at once, saying what it
    /// answered: the agent is not asked in a turn that was never cut.
    /// </summary>
    [Fact]
    public async Task AnInterruptRefused_EndsHandedOff_SayingSo()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true, ["refuseInterrupt"] = true }]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        const string Why = "Claude Code refused the interrupt: no interrupt is taken here";
        Assert.Equal((AgentRunStatus.HandedOff, new AgentHandoff(WhyNone: Why)), (result.Status, result.Handoff));
        Assert.Equal(new HandoffAnswered(false, Why), Assert.Single(sink.Of<HandoffAnswered>()));
        Assert.DoesNotContain(sink.Of<MessageSent>(), message => message.Which == AgentMessageKind.HandoffQuestion);
    }

    /// <summary>
    /// A handoff turn that answers nothing is asked again, up to the most answers one asking may take, and then the run
    /// ends <see cref="AgentRunStatus.HandedOff"/> without one, saying how many: a pause is never turned into a loop.
    /// </summary>
    [Fact]
    public async Task AHandoffAnsweredEmpty_IsAskedAgainUpToItsMostAnswers_AndEndsWithoutOne()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true }, new JsonObject { ["text"] = string.Empty }, new JsonObject { ["text"] = string.Empty }]);
        var run = kit.Run() with { Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(60), MaxAnswers: 2) };
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted { Turn: 1 })
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        const string Why = "the agent gave no handoff in 2 answers";
        Assert.Equal((AgentRunStatus.HandedOff, new AgentHandoff(WhyNone: Why), 3), (result.Status, result.Handoff, result.Turns));
        Assert.Equal(2, sink.Of<MessageSent>().Count(message => message.Which == AgentMessageKind.HandoffQuestion));
        Assert.Equal(new HandoffAnswered(false, Why), Assert.Single(sink.Of<HandoffAnswered>()));
    }

    /// <summary>
    /// A launch that ends while it is asked for its handoff ends the run <see cref="AgentRunStatus.HandedOff"/> at once,
    /// with no answer and why: nothing waits on a launch that has gone.
    /// </summary>
    [Fact]
    public async Task ALaunchThatEndsWhileAskedForItsHandoff_EndsHandedOff_SayingItHadEnded()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true }, new JsonObject { ["exit"] = 3 }]);
        var run = kit.Run();
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted { Turn: 1 })
            {
                run.Handoff.Ask();
            }
        });

        var result = await kit.Client().RunAsync(run, sink, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        const string Why = "the client had already ended";
        Assert.Equal((AgentRunStatus.HandedOff, new AgentHandoff(WhyNone: Why)), (result.Status, result.Handoff));
        Assert.Equal(new HandoffAnswered(false, Why), Assert.Single(sink.Of<HandoffAnswered>()));
    }

    /// <summary>
    /// What a launch's turns cost is its last result's running total, never their sum: two turns of one launch, the
    /// second reporting the launch's total as the first and the second's together, cost that total.
    /// </summary>
    [Fact]
    public async Task TwoTurnsOfOneLaunch_CostWhatItsLastResultSays()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["text"] = "first" }, new JsonObject { ["text"] = "second" }]);
        var run = kit.Run();
        run.Messages.Post("Also say second.");

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.Equal((AgentRunStatus.Completed, 2), (result.Status, result.Turns));
        Assert.Equal(new AgentCost(Reported: 0.02m), result.Usage?.Cost);
    }

    /// <summary>
    /// A run the client cannot launch never began, saying why, and nothing is started: no model to name, no program, no
    /// policy to decide its calls by, and no value for the key its way of signing in takes.
    /// </summary>
    [Theory]
    [InlineData("model", AgentNotStartedBecause.ConfigurationWrong, "the step names no model, and a launch is told its model by its id")]
    [InlineData("program", AgentNotStartedBecause.ProgramMissing, "no Claude Code program was found ready for this launch")]
    [InlineData("ground", AgentNotStartedBecause.ConfigurationWrong, "the step was given no policy to decide its calls by, and no call runs undecided")]
    [InlineData("keys", AgentNotStartedBecause.KeyMissing, "the launch signs in with auth: apiKey, and the slot holds no value for it")]
    public async Task ARunTheClientCannotLaunch_NeverBegan_SayingWhy(string missing, AgentNotStartedBecause because, string why)
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        var run = missing switch
        {
            "model" => kit.Run() with { Model = null },
            "program" => kit.Run() with { Program = null },
            "ground" => kit.Run() with { Ground = null },
            _ => kit.Run(credential: new AgentCredential(AgentAuth.ApiKey, [])),
        };

        var result = await kit.Client().RunAsync(run, new Sink(), Token);

        Assert.Equal((AgentRunStatus.NotStarted, because, $"client 'claude-code' was not started: {why}"), (result.Status, result.NotStartedBecause, result.Error));
        Assert.Empty(kit.Runner.Requests);
    }

    /// <summary>
    /// A turn of a step held to a schema that gives no answer of it is sent back as any answer that does not read is, and
    /// ends <see cref="AgentRunStatus.InvalidOutput"/> once its repairs are spent, saying it gave none.
    /// </summary>
    [Fact]
    public async Task ATurnThatGivesNoAnswerToItsSchema_IsSentBack_AndEndsInvalidOutput()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["answer"] = null }, new JsonObject { ["answer"] = null }, new JsonObject { ["answer"] = null }]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(new AgentOutput("o", Schema)), sink, Token);

        Assert.Equal(
            (AgentRunStatus.InvalidOutput, "the answer did not read against its schema after 2 repairs: $: the answer holds no output held to the schema; answer with one"),
            (result.Status, result.Error));
        Assert.Null(result.StructuredOutput);
        Assert.Equal(2, sink.Of<OutputRepair>().Count);
    }

    /// <summary>
    /// A launch that works before its init says what it runs as never started, and is ended at once - one that would not
    /// end by itself included: its tools and its credential were never held to the launch's, so nothing it did is taken.
    /// </summary>
    [Fact]
    public async Task ALaunchThatWorksBeforeItsInit_NeverStarted_AndIsEndedAtOnce()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["noInit"] = true, ["ignoreEof"] = true }]);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(), sink, Token).WaitAsync(TimeSpan.FromSeconds(60), Token);

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ProgramMissing), (result.Status, result.NotStartedBecause));
        Assert.Equal(
            "client 'claude-code' was not started: claude began its work before its init said what it runs as, so its tools and its credential were never held to this launch's: this tool runs Claude Code only as its host",
            result.Error);
        Assert.Empty(sink.Of<SessionStarted>());
        Assert.Empty(sink.Of<TurnStarted>());
    }

    /// <summary>
    /// A session file a launch kept is removed however the launch ended - a hard stop among the ends - and said, once the
    /// launch has gone.
    /// </summary>
    [Fact]
    public async Task ASessionFileTheLaunchLeft_IsRemovedThoughTheRunWasStopped()
    {
        using var kit = new Kit();
        var config = kit.Temp.Combine("claude-config");
        kit.Script([new JsonObject { ["sessionFile"] = "proj", ["calls"] = new JsonArray(new JsonObject { ["tool"] = "Read", ["input"] = new JsonObject { ["file_path"] = "a.txt" } }), ["hang"] = true }]);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var sink = new Sink(@event =>
        {
            if (@event is ToolCallFinished)
            {
                _ = stop.CancelAsync();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kit.Client().RunAsync(kit.Run(credential: new AgentCredential(AgentAuth.Login, [], ConfigDirectory: config)), sink, stop.Token).WaitAsync(TimeSpan.FromSeconds(60), Token));

        var session = Value(Assert.Single(kit.Launches()).Arguments, "--session-id");
        var file = Path.Combine(config, "projects", "proj", session + ".jsonl");
        Assert.False(File.Exists(file), "the session file the launch kept is still there");
        Assert.Equal($"Claude Code kept a session file though told to keep none, and it was removed: '{file}'", Assert.Single(sink.Of<Notice>()).Text);
    }

    /// <summary>
    /// A directory a session file could not be looked for in is said, for a file the launch kept may be in it; the step's
    /// work stands.
    /// </summary>
    [Fact]
    public async Task ADirectoryASessionFileCouldNotBeLookedForIn_IsSaid()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        var projects = Directory.CreateDirectory(Path.Combine(kit.ClaudeDirectory, "projects")).FullName;
        kit.FileSystem = new UnlistableFileSystem(kit.Harness.FileSystem, projects);
        var sink = new Sink();

        var result = await kit.Client().RunAsync(kit.Run(), sink, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(
            $"A session file Claude Code may have kept though told to keep none could not be looked for in '{projects}': Access to the path '{projects}' is denied",
            Assert.Single(sink.Of<Notice>()).Text);
    }

    /// <summary>
    /// What a launch's container could not reach when the launch ended is said - in the stream and in the result - for
    /// what the launch started may still run.
    /// </summary>
    [Fact]
    public async Task WhatALaunchsContainerCouldNotReach_IsSaid_AndKeptInTheResult()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        const string Unreached = "the process table could not be read to end its session (no table here): its group was ended, and what it started outside the group was not reached";
        var sink = new Sink();

        var result = await kit.Client(new Unreaching(kit.Runner, Unreached)).RunAsync(kit.Run(), sink, Token);

        const string Said = $"Claude Code's launch ended, and {Unreached}";
        Assert.Equal((AgentRunStatus.Completed, Said), (result.Status, Assert.Single(result.Uncontained)));
        Assert.Equal(Said, Assert.Single(sink.Of<Notice>()).Text);
    }

    /// <summary>
    /// What a stopped launch's container could not reach is said too: the stop leaves what the launch started as running
    /// as an end does.
    /// </summary>
    [Fact]
    public async Task WhatAStoppedLaunchsContainerCouldNotReach_IsSaid()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject { ["hang"] = true, ["ignoreEof"] = true }]);
        const string Unreached = "the process table could not be read to end its session (no table here): its group was ended, and what it started outside the group was not reached";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var sink = new Sink(@event =>
        {
            if (@event is TurnStarted)
            {
                _ = stop.CancelAsync();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kit.Client(new StoppedUnreaching(kit.Runner, Unreached)).RunAsync(kit.Run(), sink, stop.Token).WaitAsync(TimeSpan.FromSeconds(60), Token));

        Assert.Equal($"Claude Code's launch ended, and {Unreached}", Assert.Single(sink.Of<Notice>()).Text);
    }

    /// <summary>The files under <paramref name="directory"/> that hold <paramref name="text"/>, read beside any writer.</summary>
    private static List<string> Holding(string directory, string text)
        => [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(file =>
        {
            using var beside = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reading = new StreamReader(beside);
            return reading.ReadToEnd().Contains(text, StringComparison.Ordinal);
        })];

    /// <summary>The value after <paramref name="name"/> in an argument list.</summary>
    private static string Value(IReadOnlyList<string> argv, string name)
        => argv[argv.ToList().IndexOf(name) + 1];

    /// <summary>One launch as the fake recorded it: its start, and every line it read.</summary>
    private sealed record Recorded(IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment, IReadOnlyList<JsonObject> Read);

    /// <summary>A sink that keeps every event, and hands each to a test's own reaction as it comes.</summary>
    private sealed class Sink(Action<AgentEvent>? reaction = null) : IAgentEventSink
    {
        public List<AgentEvent> Events { get; } = [];

        public ValueTask EmitAsync(AgentEvent @event)
        {
            Events.Add(@event);
            reaction?.Invoke(@event);
            return ValueTask.CompletedTask;
        }

        public List<T> Of<T>() where T : AgentEvent => [.. Events.OfType<T>()];
    }

    /// <summary>
    /// The process runner, telling the child it starts to play the fake <c>claude</c> from the kit's script and record
    /// itself, and keeping every request it was given. The child inherits the kit's own environment - the tool's, as the
    /// client is told it - under the request's, as a child inherits its parent's: a name the request takes away is gone.
    /// </summary>
    private sealed class FakeRunner(Kit kit) : IProcessRunner
    {
        private readonly ProcessRunner _inner = new(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);

        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            var environment = new Dictionary<string, string?>(request.Environment, StringComparer.Ordinal)
            {
                [TestHost.ChildModeVariable] = "claude-code",
                [FakeClaude.ScriptVariable] = kit.ScriptFile,
                [FakeClaude.RecordVariable] = kit.RecordFile,
            };

            foreach (var (name, value) in kit.Own)
            {
                environment.TryAdd(name, value);
            }

            return _inner.RunAsync(request with { Environment = environment }, cancellationToken);
        }

        public string? FindExecutable(string command) => _inner.FindExecutable(command);
    }

    /// <summary>
    /// The client, but for each result it answers, which is held together (<see cref="AgentRunResult.Problem"/>) before a
    /// test reads it: no ending of the client's is one the runner would refuse to write.
    /// </summary>
    private sealed class HeldTogether(IAgentClient inner) : IAgentClient
    {
        public string Name => inner.Name;

        public AgentClientCapabilities Capabilities => inner.Capabilities;

        public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(run, events, cancellationToken);

            Assert.Null(result.Problem());

            return result;
        }
    }

    /// <summary>
    /// The kit's process runner, but for a launch stopped while its container could not reach all it held: the stop says
    /// so, and what, as the process runner's own stop does.
    /// </summary>
    private sealed class StoppedUnreaching(IProcessRunner inner, string unreached) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.RunAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new ProcessStoppedException(unreached, cancellationToken);
            }
        }

        public string? FindExecutable(string command) => inner.FindExecutable(command);
    }

    /// <summary>
    /// The kit's process runner, but for a launch whose container could not reach all it held when the launch ended, as a
    /// session ended on Linux or macOS without its process table is: its result says so, and what.
    /// </summary>
    private sealed class Unreaching(IProcessRunner inner, string unreached) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => await inner.RunAsync(request, cancellationToken) with { Uncontained = unreached };

        public string? FindExecutable(string command) => inner.FindExecutable(command);
    }

    /// <summary>
    /// The machine's clock, but for the bounds a test gives others: a wait of a bound named here waits its other span
    /// instead, so a bound of 30 s is reached in one and a bound that would end a launch is never reached in a test.
    /// </summary>
    private sealed class BoundsClock(params (TimeSpan Bound, TimeSpan Instead)[] bounds) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            foreach (var (bound, instead) in bounds)
            {
                if (bound == dueTime)
                {
                    return TimeProvider.System.CreateTimer(callback, state, instead, period);
                }
            }

            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>
    /// A file system whose reading of the links of one file fails as no policy expects it to: a fault of the engine's own,
    /// not a path it cannot read, which the policy answers undecided.
    /// </summary>
    private sealed class FaultsOn(DssHarness.Core.FileSystem.IFileSystem inner, string file) : PassThroughFileSystem(inner)
    {
        public override string ResolveLinks(string path)
            => Path.GetFileName(path) == file ? throw new InvalidOperationException($"'{path}' cannot be looked at") : base.ResolveLinks(path);
    }

    /// <summary>A mask that masks nothing.</summary>
    private sealed class NoMask : ILineMask
    {
        public string Redact(string? text) => text ?? string.Empty;

        public int Cut(string text) => text.Length;
    }

    /// <summary>A run's places, its script and its record, and the client under test.</summary>
    private sealed class Kit : IDisposable
    {
        public Kit()
        {
            Worktree = Directory.CreateDirectory(Temp.Combine("tree")).FullName;
            WorkDirectory = Directory.CreateDirectory(Temp.Combine("work")).FullName;
            StepDirectory = Path.Combine(Temp.Path, ".orchestrators", "r1", "agents", "a1", "steps", "s1");
            ScriptFile = Temp.Combine("script.json");
            RecordFile = Temp.Combine("record.jsonl");
            File.WriteAllText(Path.Combine(Worktree, "a.txt"), "alpha\n");
            ClaudeDirectory = Directory.CreateDirectory(Temp.Combine("claude-home")).FullName;
            Own = new(StringComparer.Ordinal) { [ClaudeTranscripts.ConfigDirectoryVariable] = ClaudeDirectory };
            Runner = new FakeRunner(this);
        }

        public TempDirectory Temp { get; } = new();

        /// <summary>The Claude Code directory the tool's own environment names, so no test reads the person's own.</summary>
        public string ClaudeDirectory { get; }

        /// <summary>The tool's own environment, as the client is told it and the child inherits it.</summary>
        public Dictionary<string, string> Own { get; }

        /// <summary>The clock the client runs on: the machine's, unless a test gives its bounds others.</summary>
        /// <summary>
        /// The clock the client is given: the bound a launch is given to end in by itself is never reached, so a launch the
        /// client should end at once and does not holds its test until the test's own bound, which fails it.
        /// </summary>
        public TimeProvider Clock { get; set; } = new BoundsClock((ClaudeCodeClient.EndBound, TimeSpan.FromMinutes(10)));

        /// <summary>The file system the client runs on: the machine's, unless a test gives it one that fails.</summary>
        public DssHarness.Core.FileSystem.IFileSystem? FileSystem { get; set; }

        public HarnessFactory Harness { get; } = new();

        public string Worktree { get; }

        public string WorkDirectory { get; }

        public string StepDirectory { get; }

        public string ScriptFile { get; }

        public string RecordFile { get; }

        public FakeRunner Runner { get; }

        public void Script(JsonNode script) => File.WriteAllText(ScriptFile, script.ToJsonString());

        public void Script(JsonObject[] turns) => Script(new JsonArray([.. turns]));

        /// <summary>
        /// The client under test, starting its launches through <paramref name="processes"/> - the kit's own runner where
        /// none is given - and every result it answers held together as the runner holds it.
        /// </summary>
        public IAgentClient Client(IProcessRunner? processes = null)
            => new HeldTogether(new ClaudeCodeClient(processes ?? Runner, FileSystem ?? Harness.FileSystem, Harness.Platform, Harness.ChildSessions, Clock, () => Own));

        /// <summary>The agent runner over the kit's client, its records on the machine's disk, run as a user's process.</summary>
        public AgentRunner StepRunner() => new([Client()], Harness.FileSystem, Elevation.User, TimeProvider.System);

        public AgentPolicyGround Ground()
            => PolicyGrounds.Of(Worktree, [Worktree, WorkDirectory, Temp.Path], [Worktree, WorkDirectory], [], [], []);

        public AgentWorkspace Workspace()
            => new(Worktree, WorkDirectory, Temp.Path, [], Worktree, Path.Combine(Temp.Path, ".orchestrators", "r1"));

        public AgentRun Run(AgentOutput? output = null, AgentCredential? credential = null)
            => new(
                "be terse",
                "do the work",
                Workspace(),
                output,
                null,
                new AgentLimits(30, 2.5m, null, null),
                "anthropic",
                Model,
                "high",
                [],
                [],
                [],
                new NoMask(),
                StepDirectory,
                new AgentHandoffSignal(TimeSpan.FromSeconds(60)),
                new AgentMessages())
            {
                Ground = Ground(),
                Credential = credential,
                Program = TestHost.AssemblyProgram,
            };

        public AgentStepRequest Step()
            => new(
                "r1",
                "a1",
                "s1",
                1,
                null,
                "anthropic",
                ClaudeCodeClient.ClientName,
                null,
                Model,
                "high",
                null,
                "be terse",
                "do the work",
                new Dictionary<string, string>(),
                null,
                Workspace(),
                null,
                null,
                new AgentLimits(30, 2.5m, null, null),
                MayAsk: false,
                AskRounds: 0,
                null)
            {
                Ground = Ground(),
                Program = TestHost.AssemblyProgram,
            };

        public List<Recorded> Launches()
        {
            var launches = new List<Recorded>();

            foreach (var line in File.Exists(RecordFile) ? File.ReadAllLines(RecordFile) : [])
            {
                var node = JsonNode.Parse(line)!.AsObject();

                if (node["argv"] is JsonArray argv)
                {
                    launches.Add(new Recorded(
                        [.. argv.Select(argument => argument!.GetValue<string>())],
                        node["cwd"]!.GetValue<string>(),
                        node["env"]!.AsObject().ToDictionary(entry => entry.Key, entry => entry.Value?.GetValue<string>(), StringComparer.Ordinal),
                        new List<JsonObject>()));
                }
                else if (node["in"] is { } read)
                {
                    ((List<JsonObject>)launches[^1].Read).Add(JsonNode.Parse(read.GetValue<string>())!.AsObject());
                }
            }

            return launches;
        }

        public void Dispose() => Temp.Dispose();
    }
}
