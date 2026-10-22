using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

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

    /// <summary>The bound of every test's run: a client that never ends fails its test, and never holds the suite.</summary>
    private readonly CancellationTokenSource _bound = Bounded();

    private CancellationToken Token => _bound.Token;

    public void Dispose() => _bound.Dispose();

    private static CancellationTokenSource Bounded()
    {
        var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(120));

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
    /// What a launch is given of the environment: nothing of a Claude Code session the engine was started in, no name
    /// that says who it is but what its way of signing in gives, no name the configuration appoints, the names the engine
    /// controls, and the Claude Code directory it reads - under a key, the entry's key and the provider's address.
    /// </summary>
    [Fact]
    public async Task TheEnvironment_HoldsWhatItsWayOfSigningInGives_AndNothingOfAHostSession()
    {
        using var kit = new Kit();
        kit.Script([new JsonObject()]);
        var own = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLAUDE_CODE_ENTRYPOINT"] = "claude-desktop",
            ["CLAUDECODE"] = "1",
            ["PATH"] = "/bin",
        };
        var config = kit.Temp.Combine("claude-config");
        var run = kit.Run(credential: new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-work")], "https://gateway.example", config)) with
        {
            Withheld = ["ZAI_API_KEY", "WORK_KEY"],
        };

        var result = await kit.Client(() => own).RunAsync(run, new Sink(), Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var request = Assert.Single(kit.Runner.Requests);
        Assert.Equal((StreamKept.TailOfWholeLines, StreamKept.Tail, true), (request.OutputKept, request.ErrorKept, request.CutLine is not null));

        var given = request.Environment;
        Assert.Null(given["CLAUDE_CODE_ENTRYPOINT"]);
        Assert.Null(given["CLAUDECODE"]);
        Assert.Null(given["ZAI_API_KEY"]);
        Assert.False(given.ContainsKey("PATH"));
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
        Assert.Equal("sk-work", seen["ANTHROPIC_API_KEY"]);
        Assert.Equal("1", seen["DISABLE_COMPACT"]);
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
        var own = new Dictionary<string, string>(StringComparer.Ordinal) { ["ANTHROPIC_API_KEY"] = "sk-ambient", [read] = "ambient" };

        var result = await kit.Client(() => own).RunAsync(kit.Run(credential: new AgentCredential(auth, [new AgentKey("GATEWAY_TOKEN", "tok-1")])), new Sink(), Token);

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
        var runner = new AgentRunner([kit.Client()], kit.Harness.FileSystem, TimeProvider.System);

        var result = await runner.RunAsync(kit.Step(), null, Token);

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
        Assert.Equal(StructuredOutputGate.Problems(new AgentOutput("o", Schema), new JsonObject { ["answers"] = "a" }), repair.Problems);
        Assert.NotEmpty(repair.Problems);

        var sent = sink.Of<MessageSent>();
        Assert.Equal([AgentMessageKind.FirstPrompt, AgentMessageKind.Repair], sent.Select(message => message.Which));
        Assert.Equal(StructuredOutputGate.Repair(repair.Problems), sent[1].Text);
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
    [InlineData("error_max_turns", null, AgentRunStatus.MaxTurns)]
    [InlineData("error_max_budget_usd", null, AgentRunStatus.BudgetSpent)]
    [InlineData(null, "prompt_too_long", AgentRunStatus.ContextExhausted)]
    [InlineData("error_during_execution", null, AgentRunStatus.Failed)]
    public async Task ALimitALaunchEndedOn_IsTheStepsEnding(string? subtype, string? terminal, AgentRunStatus status)
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

        Assert.Equal(status, result.Status);
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

        var result = await kit.Client().RunAsync(kit.Run(credential: credential), new Sink(), Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(["KEY_ONE", "KEY_TWO"], result.KeysRefused);
        Assert.Equal(2, kit.Launches().Count);
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

        var result = await kit.Client().RunAsync(kit.Run() with { Program = kit.Temp.Combine("no-claude.exe") }, new Sink(), Token);

        Assert.Equal(AgentRunStatus.NotStarted, result.Status);
        Assert.Equal(AgentNotStartedBecause.ProgramMissing, result.NotStartedBecause);
    }

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
    /// itself, and keeping every request it was given.
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

            return _inner.RunAsync(request with { Environment = environment }, cancellationToken);
        }

        public string? FindExecutable(string command) => _inner.FindExecutable(command);
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
            Runner = new FakeRunner(this);
        }

        public TempDirectory Temp { get; } = new();

        public HarnessFactory Harness { get; } = new();

        public string Worktree { get; }

        public string WorkDirectory { get; }

        public string StepDirectory { get; }

        public string ScriptFile { get; }

        public string RecordFile { get; }

        public FakeRunner Runner { get; }

        public void Script(JsonNode script) => File.WriteAllText(ScriptFile, script.ToJsonString());

        public void Script(JsonObject[] turns) => Script(new JsonArray([.. turns]));

        public ClaudeCodeClient Client(Func<IReadOnlyDictionary<string, string>>? environment = null)
            => new(Runner, Harness.FileSystem, Harness.Platform, Harness.ChildSessions, TimeProvider.System, environment);

        public AgentPolicyGround Ground()
            => new(Worktree, [Worktree, WorkDirectory, Temp.Path], [Worktree, WorkDirectory], [], [], []);

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
                [],
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
