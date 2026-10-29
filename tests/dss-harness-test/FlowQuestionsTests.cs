using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// Every kind of question, its answers, and the answers a configuration gives: the nearest <c>answers</c> with
/// <c>times</c> and <c>then</c>, what a <c>fail</c> answer ends a step with, the kinds no configuration answers, the
/// fold family's questions over the verbs' own facts, and a <c>pause</c> step's answer with its text - all over fake
/// step runners, in memory.
/// </summary>
public sealed class FlowQuestionsTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A runner that answers each step as its test scripted it.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>Every run asked, whole.</summary>
        public List<FlowStepRun> Runs { get; } = [];

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => Task.FromResult(result);
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = run => Task.FromResult(script(run));
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by its script's latest word: a queue of results, each asked once.</summary>
        public FakeRunner Queue(string step, params FlowStepResult[] results)
        {
            var at = 0;
            _scripts[step] = run => Task.FromResult(results[Math.Min(at++, results.Length - 1)]);
            return this;
        }

        /// <inheritdoc/>
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add(run.Instance);
            Runs.Add(run);

            return _scripts.GetValueOrDefault(run.Step.Name, _ => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>A step's output, read from JSON text.</summary>
    private static JsonNode? Output(string json)
        => JsonNode.Parse(json);

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>
    /// A fold family step's dry run, as its runner reports it over the verbs' own facts: each consent about the name at its
    /// place among <paramref name="named"/>, and a refusal's names the paths its merges conflict on.
    /// </summary>
    private static FlowStepResult Dry(RequestStanding standing, ConsentKind[] consents, string[] named, RefusalKind[] refusals, string summary)
        => FlowStepResult.Standing(new FlowDryRun(standing, consents, [.. consents.Select((consent, at) => new FlowNamed(consent, at < named.Length ? [named[at]] : []))], refusals, summary)
        {
            Conflicting = standing == RequestStanding.Refused ? named : [],
        });

    /// <summary>
    /// A question's record read back as the question the engine holds and written again is the same record, every member
    /// of it - who raised it and the agent, what an agent asked, the answers it takes with their texts, every answer with
    /// who gave it, through what, where, from which input and when it was replaced, the recommendation and the handler a
    /// bound is about: the one pair of conversions loses nothing, so no later write of the run's file ever does.
    /// </summary>
    [Fact]
    public void AQuestionsRecord_ReadAsAQuestionAndWrittenAgain_IsTheSameRecord()
    {
        var at = new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.Zero);
        var entry = new FlowQuestionEntry(
            "q7",
            "answered",
            "visitBound",
            at,
            [new AgentQuestion("which way?", "two ways stand", "the map", "the route", ["left", "right"], "left", "it is shorter")],
            [new FlowQuestionOption("more", "one more of what was bounded"), new FlowQuestionOption("direct", "the fix given", Text: true), new FlowQuestionOption("abort", "the run ends")],
            [
                new FlowAnswerRecord("more", at.AddMinutes(1), "go on", new FlowAnswerBy("person", "front-end"), "one more visit", at.AddMinutes(5)),
                new FlowAnswerRecord("more", at.AddMinutes(2), By: new FlowAnswerBy("configuration", Where: "step 'fix'")),
                new FlowAnswerRecord("accept", at.AddMinutes(3), By: new FlowAnswerBy("input", Input: "preapproved")),
            ],
            new FlowQuestionRaisedBy("step", "rounds#2.items[1].build", "a3"),
            new FlowQuestionProblem("it failed again", "two visits of the fix", "'ship' waits on this"),
            new FlowRecommendation("given", "more", "go on", "the last visit narrowed it", "advisor", "advisor", "the advisor's model", "steps/build/advice/1"),
            "fix");

        var again = FlowQuestion.FromEntry(entry).ToEntry();

        Assert.Equal(JsonSerializer.Serialize(entry, JsonStateFile.Options), JsonSerializer.Serialize(again, JsonStateFile.Options));
    }

    /// <summary>
    /// The questions an agent or a step's output wrote are held, in a run's questions, to their record's own rule, as the
    /// answer that held them was: every text written, options no two alike, the recommendation among them.
    /// </summary>
    [Fact]
    public void TheQuestionsAnAgentWrote_AreHeldToTheirRecordsOwnRule()
    {
        FlowQuestionEntry Entry(params AgentQuestion?[] asked)
            => new(
                "q1",
                "open",
                "pause",
                new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.Zero),
                asked!,
                [new FlowQuestionOption("approve", "the plan goes on"), new FlowQuestionOption("revise", "the plan is written again")],
                []);
        var asked = new AgentQuestion("which way?", "two ways stand", "the map", "the route", ["left", "right"], "left", "it is shorter");

        Assert.Null(Entry(asked).Problem());
        Assert.Equal("its questions[1].tried: is empty; say it, as the schema describes it", Entry(asked, asked with { Tried = "\t" }).Problem());
        Assert.Equal("its questions[0].options: holds 1 option, and a question offers at least 2", Entry(asked with { Options = ["left"] }).Problem());
        Assert.Equal("its questions[0]: is null", Entry([null]).Problem());
        Assert.Equal(
            "its question 'q1': its questions[0].recommended: \"up\" is none of the 2 options; recommend one of them, word for word",
            new FlowQuestionsFile(1, "r1", new DateTimeOffset(2026, 10, 21, 8, 30, 0, TimeSpan.Zero), [Entry(asked with { Recommended = "up" })]).Problem());
    }

    /// <summary>Each kind's answers, as the coordinator offers them: the kind's own, and skip only where the step is skippable.</summary>
    [Fact]
    public void EachKindOffersItsOwnAnswers_AndSkipOnlyWhereTheStepIsSkippable()
    {
        var skippable = new WorkflowStep { Name = "s", Skippable = true };
        var plain = new WorkflowStep { Name = "s" };

        Assert.Equal(["retry", "more", "skip", "fail", "abort"], FlowQuestions.OptionsOf("turnLimit", skippable));
        Assert.Equal(["retry", "more", "fail", "abort"], FlowQuestions.OptionsOf("turnLimit", plain));
        Assert.Equal(["retry", "compact", "fail", "abort"], FlowQuestions.OptionsOf("contextLimit", plain));
        Assert.Equal(["merge", "retry", "fail", "abort"], FlowQuestions.OptionsOf("foldTwoChanges", plain));
        Assert.Equal(["retry", "fail", "abort"], FlowQuestions.OptionsOf("foldConflicts", plain));
        Assert.Equal(["more", "accept", "fail", "abort"], FlowQuestions.OptionsOf("checksPending", plain));
        Assert.Equal(["retry", "accept", "fail", "abort"], FlowQuestions.OptionsOf("noChecks", plain));
        Assert.Equal(["retry", "abort", "skip"], FlowQuestions.OptionsOf("failure", skippable));
        Assert.Equal(["retry", "abort"], FlowQuestions.OptionsOf("failure", plain));
        Assert.Equal(["approve", "revise"], FlowQuestions.OptionsOf("pause", new WorkflowStep { Name = "s", Options = ["approve", "revise"] }));
    }

    /// <summary>A dry run that lacks consents raises the question of the first it lacks, a person's first among them.</summary>
    [Fact]
    public async Task ADryRunNeedingConsents_AsksOneAtATime_APersonsFirst()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fold
                type: fold
                of: made
              - name: made
                type: create-agent
                agent: worker.md
            """);
        var runner = new FakeRunner().On(
            "fold",
            Dry(
                RequestStanding.NeedsConsent,
                [ConsentKind.FoldNewRows, ConsentKind.FoldLostRows],
                ["row 12", "cell x"],
                [],
                "2 new rows, 1 lost cell"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var question = coordinator.OpenQuestions.Single();
        Assert.Equal("foldLostRows", question.Kind);
        Assert.Equal(["allow", "retry", "abort"], question.Options);
        Assert.Contains("2 new rows, 1 lost cell", question.Problem);
    }

    /// <summary>A dry run refused for conflicting merges alone raises foldConflicts, which is never answered merge; any other refusal fails the step 13.</summary>
    [Fact]
    public async Task ConflictingMergesAloneAskFoldConflicts_AnyOtherRefusalFailsTheStep()
    {
        var conflicts = Parse("""
            name: flow
            steps:
              - name: fold
                type: fold
                of: made
              - name: made
                type: create-agent
                agent: worker.md
            """);
        var conflictRunner = new FakeRunner().On(
            "fold",
            Dry(RequestStanding.Refused, [], ["src/A.cs", "src/B.cs"], [RefusalKind.Conflict, RefusalKind.Conflict], "2 paths conflict"));
        var conflict = new FlowCoordinator(conflicts, conflictRunner, new RunnerFacts(), new JumpingClock());

        var end = await conflict.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var question = conflict.OpenQuestions.Single();
        Assert.Equal("foldConflicts", question.Kind);
        Assert.Equal(["retry", "fail", "abort"], question.Options);
        Assert.Contains("src/A.cs", question.Problem);

        var refused = Parse("""
            name: flow
            steps:
              - name: fold
                type: fold
                of: made
                onFailure: continue
              - name: made
                type: create-agent
                agent: worker.md
            """);
        var refusedRunner = new FakeRunner().On(
            "fold",
            Dry(RequestStanding.Refused, [], [], [RefusalKind.Conflict, RefusalKind.Other], "a path is refused"));
        var failed = new FlowCoordinator(refused, refusedRunner, new RunnerFacts(), new JumpingClock());

        var other = await failed.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, other.Status);
        Assert.Empty(failed.OpenQuestions);
        var fold = failed.Instances["fold"];
        Assert.Equal(FlowInstanceState.Failed, fold.State);
        Assert.Equal(HarnessExit.Refused, fold.Exit);
        Assert.Equal("a path is refused", fold.Error);
    }

    /// <summary>The nearest answers wins: the step's own, then each group around it from the inside out, then the workflow's.</summary>
    [Fact]
    public async Task TheNearestAnswersWins_TheStepsThenTheGroupsThenTheOrchestrators()
    {
        var definition = Parse("""
            name: flow
            answers:
              loopBound: more
            steps:
              - name: rounds
                repeat:
                  until: done.ok
                  maxIterations: 1
                steps:
                  - name: done
                    type: orient
            """);
        var runner = new FakeRunner().On("done", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The workflow's answers gave one more; with no more to give and no then, the question is a person's.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal("loopBound", coordinator.OpenQuestions.Single().Kind);
        Assert.Equal(2, coordinator.Instances["rounds"].Children.Count);
    }

    /// <summary>A group's answers stand between its members' and the workflow's: a member with none of its own reads its group's.</summary>
    [Fact]
    public async Task AGroupSAnswersStandBetweenItsMembers_AndTheOrchestrators()
    {
        var definition = Parse("""
            name: flow
            answers:
              loopBound: more
            steps:
              - name: work
                steps:
                  - name: rounds
                    repeat:
                      until: done.ok
                      maxIterations: 1
                    steps:
                      - name: done
                        type: orient
                answers:
                  loopBound: accept
            """);
        var runner = new FakeRunner().On("done", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The group's accept ended the loop as it stood; no person was asked and no more iteration ran.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        var rounds = coordinator.Instances["work.rounds"];
        Assert.Single(rounds.Children);
        Assert.Equal(FlowInstanceState.Done, rounds.State);
        var given = coordinator.Questions.Single().Given.Single();
        Assert.Equal("accept", given.Answer);
        Assert.Equal("group 'work'", given.Where);
    }

    /// <summary>A counted answer is given its times and no more, and then what then names - and where none is named the question is a person's.</summary>
    [Fact]
    public async Task ACountedAnswerIsGivenItsTimes_ThenItsThen_OrAskedOfAPerson()
    {
        var definition = Parse("""
            name: flow
            answers:
              loopBound:
                answer: more
                times: 2
                then: accept
            steps:
              - name: rounds
                repeat:
                  until: done.ok
                  maxIterations: 1
                steps:
                  - name: done
                    type: orient
            """);
        var runner = new FakeRunner().On("done", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // Two mores were given (three iterations in all) and the third bound was answered accept: nobody was asked.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Empty(coordinator.OpenQuestions);
        var rounds = coordinator.Instances["rounds"];
        Assert.Equal(3, rounds.Children.Count);
        Assert.Equal(FlowInstanceState.Done, rounds.State);

        // Every one of the three questions was answered by the configuration, and says so with where it is configured.
        Assert.Equal(3, coordinator.Questions.Count);
        Assert.All(coordinator.Questions, question =>
        {
            Assert.Equal("answered", question.State);
            var given = question.Given.Single();
            Assert.Equal("configuration", given.By);
            Assert.Equal("the orchestrator's answers", given.Where);
        });
        Assert.Equal(["more", "more", "accept"], coordinator.Questions.Select(question => question.Given[0].Answer));
    }

    /// <summary>The configuration's answer is given at once, and the step is never paused for it.</summary>
    [Fact]
    public async Task AConfiguredAnswerIsGivenAtOnce_AndTheStepIsNeverPausedForIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fold
                type: fold
                of: made
                answers:
                  foldNewRows: allow
              - name: made
                type: create-agent
                agent: worker.md
            """);
        var runner = new FakeRunner().Queue(
            "fold",
            Dry(RequestStanding.NeedsConsent, [ConsentKind.FoldNewRows], ["row 12"], [], "1 new row"),
            FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"rows": ["12"]}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        var fold = coordinator.Instances["fold"];
        Assert.Equal(FlowInstanceState.Done, fold.State);
        Assert.Single(coordinator.Questions);
        Assert.Equal("answered", coordinator.Questions[0].State);
        Assert.Equal("allow", coordinator.Questions[0].Given[0].Answer);
        Assert.Equal("step 'fold'", coordinator.Questions[0].Given[0].Where);
        // The answer was handed to the runner, in the same attempt.
        var again = runner.Runs.Last(run => run.Step.Name == "fold");
        Assert.Equal(1, again.Attempt);
        Assert.Equal(["allow"], again.Answers.Select(answer => answer.Answer));
    }

    /// <summary>A lost row and a cell both changed are a person's alone: no answers anywhere give them.</summary>
    [Fact]
    public async Task ALostRowAndAnOverCellAreAPersons_WhateverTheConfigurationSays()
    {
        var definition = Parse("""
            name: flow
            answers:
              foldLostRows: allow
            steps:
              - name: fold
                type: fold
                of: made
                answers:
                  foldOverCells: allow
              - name: made
                type: create-agent
                agent: worker.md
            """);
        var runner = new FakeRunner().Queue(
            "fold",
            Dry(RequestStanding.NeedsConsent, [ConsentKind.FoldLostRows], ["cell x"], [], "1 lost cell"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var question = coordinator.OpenQuestions.Single();
        Assert.Equal("foldLostRows", question.Kind);
        Assert.Empty(question.Given);
    }

    /// <summary>
    /// A configured answer is given only where the question offers it: a then of skip, on a step that is not skippable,
    /// is no answer the question takes, so a person is asked - the step is never ended skipped against its own word.
    /// </summary>
    [Fact]
    public async Task AConfiguredAnswerTheQuestionDoesNotOffer_IsAskedOfAPerson()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                answers:
                  turnLimit:
                    answer: retry
                    times: 1
                    then: skip
              - name: after
                type: orient
                dependsOn: [think]
            """);
        var runner = new FakeRunner().On("think", FlowStepResult.AtLimit("turns", "12 turns"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The counted retry was given once; the then is no answer of this question, so it is open for a person.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = Assert.Single(coordinator.OpenQuestions);
        Assert.Equal("turnLimit", asked.Kind);
        Assert.Empty(asked.Given);
        Assert.Equal(FlowInstanceState.Paused, coordinator.Instances["think"].State);
        Assert.Equal(["think", "think"], runner.Started);
        Assert.False(coordinator.Instances.ContainsKey("after"));
    }

    /// <summary>A pause step answered with no text holds an empty text, as the plan says: a prompt that reads it reads nothing, never "null".</summary>
    [Fact]
    public async Task APauseAnsweredWithNoText_HoldsAnEmptyText()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: approval
                type: pause
                question: may it go ahead?
                options: [approve, revise]
            """);
        var coordinator = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock());

        await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "approve", out _));

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(string.Empty, coordinator.Instances["approval"].Output!["text"]!.GetValue<string>());
    }

    /// <summary>A fail answer ends the step failed with the question's text, its kind as its question fact, and the code the table of codes gives.</summary>
    [Fact]
    public async Task AFailAnswerEndsTheStepFailed_WithItsKindAndCode_AndTheOnFailureDecides()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onFailure: continue
                answers:
                  turnLimit: fail
              - name: reads
                type: orient
                dependsOn: [think]
                when: think.question == 'turnLimit'
            """);
        var runner = new FakeRunner().On("think", FlowStepResult.AtLimit("turns", "12 turns"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var think = coordinator.Instances["think"];
        Assert.Equal(FlowInstanceState.Failed, think.State);
        Assert.Equal(HarnessExit.CommandFailed, think.Exit);
        Assert.Equal("turnLimit", think.EndedByQuestion);
        Assert.Contains("12 turns", think.Error);
        // The failure was passed over, not retried and not stopped for: the answer was that the step fails.
        Assert.True(think.PassedOver);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 step failed and was passed over: think", end.Summary);
        // The fact a handler reads: the kind of the question that ended it.
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["reads"].State);
    }

    /// <summary>A reason step's limit asks and is no failure: more goes on in the same attempt, retry begins a new one, compact keeps the attempt.</summary>
    [Fact]
    public async Task AReasonStepAtItsLimit_AsksAndIsNoFailure()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner().Queue(
            "think",
            FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true },
            FlowStepResult.AtLimit("context", "the conversation filled it") with { StoredConversation = true },
            FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var first = coordinator.OpenQuestions.Single();
        Assert.Equal("turnLimit", first.Kind);
        // A stored conversation of the attempt can be handed, so more of it is offered.
        Assert.Equal(["retry", "more", "fail", "abort"], first.Options);

        Assert.True(coordinator.TryAnswer(first.Id, "more", out var more));
        Assert.Null(more);

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var second = coordinator.OpenQuestions.Single();
        Assert.Equal("contextLimit", second.Kind);
        Assert.Equal(["retry", "compact", "fail", "abort"], second.Options);

        Assert.True(coordinator.TryAnswer(second.Id, "compact", out _));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // more and compact went on in the same attempt: the step finished on its first, and the whole run is done.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        var think = coordinator.Instances["think"];
        Assert.Equal(FlowInstanceState.Done, think.State);
        Assert.Equal(1, think.Attempt);
        var asked = runner.Runs.Where(run => run.Step.Name == "think").ToList();
        Assert.Equal(3, asked.Count);
        Assert.All(asked, run => Assert.Equal(1, run.Attempt));
        Assert.Equal(["more", "compact"], asked[2].Answers.Select(answer => answer.Answer));
    }

    /// <summary>
    /// A reason step's limit offers <c>more</c> only where a stored conversation of the attempt can be handed: a runner
    /// that says one is there offers it, and the answer goes on in the same attempt as every answer that lets the step's
    /// work go on does.
    /// </summary>
    [Fact]
    public async Task AReasonStepAtItsLimit_WhereAStoredConversationCanBeHanded_OffersMore()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner().Queue(
            "think",
            FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true },
            FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var first = coordinator.OpenQuestions.Single();
        Assert.Equal("turnLimit", first.Kind);
        Assert.Equal(["retry", "more", "fail", "abort"], first.Options);

        Assert.True(coordinator.TryAnswer(first.Id, "more", out _));
        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A wait for checks asks by what it found, and its fail answer ends the step with the code each reading gives.</summary>
    [Fact]
    public async Task AWaitForChecks_AsksByWhatItFound_AndFailsWithTheCodeOfItsReading()
    {
        var none = Parse("""
            name: flow
            steps:
              - name: wait
                type: ci-wait
                answers:
                  noChecks: fail
            """);
        var noneCoordinator = new FlowCoordinator(
            none,
            new FakeRunner().On("wait", FlowStepResult.Found("none", "no check appeared in 300 s")),
            new RunnerFacts(),
            new JumpingClock());

        await noneCoordinator.RunAsync(TestContext.Current.CancellationToken);

        var wait = noneCoordinator.Instances["wait"];
        Assert.Equal(FlowInstanceState.Failed, wait.State);
        Assert.Equal(2, wait.Exit);
        Assert.Equal("noChecks", wait.EndedByQuestion);

        var pending = Parse("""
            name: flow
            steps:
              - name: wait
                type: ci-wait
                answers:
                  checksPending: fail
            """);
        var pendingCoordinator = new FlowCoordinator(
            pending,
            new FakeRunner().On("wait", FlowStepResult.Found("pending", "still running at 60 minutes")),
            new RunnerFacts(),
            new JumpingClock());

        await pendingCoordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(21, pendingCoordinator.Instances["wait"].Exit);
        Assert.Equal("checksPending", pendingCoordinator.Instances["wait"].EndedByQuestion);
    }

    /// <summary>A pause step's option that names its text needs one, and one that names none refuses it; its answer and text are its output.</summary>
    [Fact]
    public async Task APauseStepOptionThatNamesItsTextNeedsIt_AndItsAnswerAndTextAreItsOutput()
    {
        var definition = Parse("""
            name: flow
            inputs:
              - name: request
                required: true
            steps:
              - name: approval
                type: pause
                question: may the plan go ahead, given {input.request}?
                options: [approve, revise]
                text: [revise]
              - name: after
                type: orient
                dependsOn: [approval]
                when: approval.text != null
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), new Dictionary<string, string> { ["request"] = "the parser item" });

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = coordinator.OpenQuestions.Single();
        Assert.Equal("pause", asked.Kind);
        Assert.Equal("may the plan go ahead, given the parser item?", asked.Problem);

        Assert.False(coordinator.TryAnswer(asked.Id, "revise", out var without));
        Assert.Equal("'revise' is an answer 'q1' takes with a text; give one", without);
        Assert.False(coordinator.TryAnswer(asked.Id, "approve", "words", out var with));
        Assert.Equal("'approve' is an answer 'q1' takes with no text; give none", with);
        Assert.True(coordinator.TryAnswer(asked.Id, "revise", "split the parser item in two", out _));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        var approval = coordinator.Instances["approval"];
        Assert.Equal(FlowInstanceState.Done, approval.State);
        Assert.Null(approval.Exit);
        Assert.Equal("""{"answer":"revise","text":"split the parser item in two"}""", approval.Output!.ToJsonString());
        // The steps after it read the text as any output.
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }

    /// <summary>A pause step that says person takes no configured answer - not its own, and none from outside it.</summary>
    [Fact]
    public async Task APauseStepThatSaysPerson_TakesNoConfiguredAnswer()
    {
        var definition = Parse("""
            name: flow
            answers:
              pause: revise
            steps:
              - name: approval
                type: pause
                question: may it go ahead?
                options: [approve, revise]
                person: true
                answers:
                  pause: approve
            """);
        var coordinator = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The step says a person answers it: neither the workflow's answer nor its own is given.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = coordinator.OpenQuestions.Single();
        Assert.Empty(asked.Given);
        Assert.True(coordinator.TryAnswer(asked.Id, "approve", out _));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("person", coordinator.Questions[0].Given[0].By);
    }

    /// <summary>A configured pause answer on the step alone is given at once; abort is taken for any question, a pause step's among them.</summary>
    [Fact]
    public async Task APauseStepIsAnsweredOnItself_Alone_AndAbortIsAlwaysTaken()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: approval
                type: pause
                question: may it go ahead?
                options: [approve, revise]
                answers:
                  pause: approve
            """);
        var coordinator = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("approve", coordinator.Instances["approval"].Output!["answer"]!.GetValue<string>());

        var abortable = Parse("""
            name: flow
            steps:
              - name: approval
                type: pause
                question: may it go ahead?
                options: [approve, revise]
            """);
        var aborting = new FlowCoordinator(abortable, new FakeRunner(), new RunnerFacts(), new JumpingClock());

        await aborting.RunAsync(TestContext.Current.CancellationToken);

        // abort stands among no options, and is taken all the same: it ends the run as a hard stop does - stopped - and
        // settles nothing: the question is open again, with the abort kept among its answers, and its step waits on it.
        Assert.True(aborting.TryAnswer(aborting.OpenQuestions.Single().Id, "abort", out _));

        end = await aborting.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
        Assert.Equal("stopped", end.Summary);
        var approval = aborting.Instances["approval"];
        Assert.Equal(FlowInstanceState.Paused, approval.State);
        var reopened = Assert.Single(aborting.OpenQuestions);
        Assert.Equal("q1", reopened.Id);
        Assert.Equal("q1", approval.WaitingQuestion);
        Assert.Equal(["abort"], reopened.Given.Select(given => given.Answer));

        // The resume waits on it, and goes on once it is answered.
        end = await aborting.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal("paused; waiting on q1", end.Summary);
        Assert.True(aborting.TryAnswer("q1", "approve", out _));

        end = await aborting.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["abort", "approve"], reopened.Given.Select(given => given.Answer));
    }

    /// <summary>A counted answer is counted per place - one step instance - so a retry it itself creates spends the same count.</summary>
    [Fact]
    public async Task ACountedAnswerIsCountedPerPlace_OfOneStepInstance()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fold
                type: fold
                of: made
                answers:
                  foldConflicts:
                    answer: retry
                    times: 2
              - name: made
                type: create-agent
                agent: worker.md
            """);
        var runner = new FakeRunner().Queue(
            "fold",
            Dry(RequestStanding.Refused, [], ["src/A.cs"], [RefusalKind.Conflict], "1 path conflicts"),
            Dry(RequestStanding.Refused, [], ["src/A.cs"], [RefusalKind.Conflict], "1 path conflicts"),
            Dry(RequestStanding.Refused, [], ["src/A.cs"], [RefusalKind.Conflict], "1 path conflicts"),
            Dry(RequestStanding.Refused, [], ["src/A.cs"], [RefusalKind.Conflict], "1 path conflicts"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        // Two conflicts were retried by the configuration, across the attempts its answers created; the third, with
        // the count spent and no then, is a person's.
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var questions = coordinator.Questions;
        Assert.Equal(3, questions.Count);
        Assert.Equal("configuration", questions[0].Given[0].By);
        Assert.Equal("configuration", questions[1].Given[0].By);
        Assert.Empty(questions[2].Given);
        Assert.Equal(3, coordinator.Instances["fold"].Attempt);
    }

    /// <summary>A question a pause step's template cannot fill fails the step, as any step its template breaks.</summary>
    [Fact]
    public async Task APauseStepWhoseQuestionCannotBeFilled_FailsTheStep()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: approval
                type: pause
                question: may {nothing.here} go ahead?
                options: [approve]
                onFailure: continue
            """);
        var coordinator = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        var approval = coordinator.Instances["approval"];
        Assert.Equal(FlowInstanceState.Failed, approval.State);
        Assert.Equal(HarnessExit.ConfigInvalid, approval.Exit);
        Assert.Equal("expression 'question' of 'approval': '{nothing.here}' reads nothing", approval.Error);
    }

    /// <summary>A pause step answered at once does not end the session over what follows it: its dependents run.</summary>
    [Fact]
    public async Task APauseStepAnsweredAtOnce_ItsDependentsRun()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: approval
                type: pause
                question: may it go ahead?
                options: [approve, revise]
                answers:
                  pause: approve
              - name: after
                type: orient
                dependsOn: [approval]
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["after"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }

    /// <summary>A pause step whose question cannot be filled, inside a group that passes failures over: the group ends, what follows runs, and the failure is counted.</summary>
    [Fact]
    public async Task AnUnfillablePauseStepInsideAGroup_TheGroupEndsAndWhatFollowsRuns()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                onFailure: continue
                steps:
                  - name: approval
                    type: pause
                    question: may {nothing.here} go ahead?
                    options: [approve]
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["after"], runner.Started);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["work.approval"].State);
        Assert.True(coordinator.Instances["work"].PassedOver);
        Assert.Contains("work", end.Summary);
    }

    /// <summary>A repeat whose until reads nothing - a member no step wrote - fails its group, as an unreadable expression fails any step.</summary>
    [Fact]
    public async Task ARepeatWhoseUntilReadsNothing_FailsItsGroup()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: done.absent
                  maxIterations: 2
                steps:
                  - name: done
                    type: orient
            """);
        var runner = new FakeRunner().On("done", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        var rounds = coordinator.Instances["rounds"];
        Assert.Equal(FlowInstanceState.Failed, rounds.State);
        Assert.Equal(HarnessExit.ConfigInvalid, rounds.Exit);
        Assert.Equal("expression 'until' of 'rounds#1': 'done.absent' reads nothing", rounds.Error);
    }

    /// <summary>An agent's questions are answered with a text: <c>answer</c> takes one, <c>decide</c> and <c>abort</c> take none.</summary>
    [Fact]
    public void AnAgentsAnswer_TakesAText_AndDecideTakesNone()
        => Assert.Equal(
            [true, false, false],
            FlowQuestions.HeldOptionsOf("ask", step: null, ["answer", "decide", "abort"]).Select(option => option.Text));

    /// <summary>
    /// The questions a pause step shows under its own are read from the upstream output its <c>questions</c> names,
    /// each element in the shape of a question; one that recommends outside its choices is shown as it is with a line
    /// under it saying so, and a list that is not questions fails the step.
    /// </summary>
    [Fact]
    public async Task APauseStepShowsTheQuestionsAnUpstreamStepWrote_UnderItsOwn()
    {
        var definition = Parse("""
            name: flow
            inputs:
              - name: request
            steps:
              - name: intake
                type: reason
                prompt: work.md
                outputs: work.json
              - name: blocked
                type: pause
                dependsOn: [intake]
                question: 'Answer continue, with what you decide for each.'
                questions: 'intake.stops'
                options: [continue]
            """);
        var runner = new FakeRunner().On("intake", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""
            {"stops": [
              {"question": "which database?", "problem": "two are configured", "tried": "the config", "means": "the item waits",
               "options": ["postgres", "sqlite"], "recommended": "postgres", "why": "it is already running"},
              {"question": "whose sign-off?", "problem": "none named", "tried": "the plan", "means": "the merge waits",
               "options": ["yours", "mine"], "recommended": "theirs", "why": "a person said so"}
            ]}
            """)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var open = coordinator.OpenQuestions.Single();

        Assert.Equal("pause", open.Kind);
        Assert.Equal("which database?", open.AgentQuestions[0].Question);
        Assert.Equal("postgres", open.AgentQuestions[0].Recommended);
        Assert.Equal("whose sign-off?", open.AgentQuestions[1].Question);

        // A recommendation outside its own choices is shown as it stands, with a line under it saying so.
        Assert.Contains("'theirs', which is not among its own choices", open.AgentQuestions[1].Why, StringComparison.Ordinal);

        Assert.True(coordinator.TryAnswer(open.Id, "continue", null, out _));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A pause step's <c>questions</c> that yields an element no question is fails the step, as a template that breaks does.</summary>
    [Fact]
    public async Task APauseStepsQuestionsThatYieldNoQuestion_FailsTheStep()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: intake
                type: reason
                prompt: work.md
                outputs: work.json
              - name: blocked
                type: pause
                dependsOn: [intake]
                question: 'Answer continue.'
                questions: 'intake.stops'
                options: [continue]
            """);
        var runner = new FakeRunner().On("intake", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"stops": ["just a string"]}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(HarnessExit.ConfigInvalid, coordinator.Instances["blocked"].Exit);
        Assert.Contains("is not a question of the shape", coordinator.Instances["blocked"].Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the person gave the run answers a pause step's question beforehand: the first entry whose <c>when</c>
    /// holds answers at once - no person asked - and an entry that quotes words holds only where they stand in the
    /// input, every run of white space as one space; reported words that do not stand there pass it by, the record
    /// says so, and the person is asked.
    /// </summary>
    [Fact]
    public async Task WhatThePersonGaveTheRun_AnswersAPauseStepBeforehand()
    {
        var definition = Parse("""
            name: flow
            inputs:
              - name: preapproved
              - name: request
            steps:
              - name: intake
                type: reason
                prompt: work.md
                outputs: work.json
              - name: approval
                type: pause
                dependsOn: [intake]
                preAnswer:
                  - when: "input.preapproved == 'yes'"
                    answer: approve
                  - when: "input.preapproved != 'no' && intake.triage.preapproval.given"
                    answer: approve
                    quote:
                      text: 'intake.triage.preapproval.sentence'
                      input: request
                question: 'Approve the plan?'
                options: [approve, revise]
                text: [revise]
            """);
        var approved = Output("""{"triage": {"preapproval": {"given": true, "sentence": "Build it without waiting for me to approve the plan."}}}""");

        // The first entry's when holds outright: answered at once, no person asked, the input named with its value -
        // and the run goes on, the pause step done with the answer as its output.
        var runner = new FakeRunner().On("intake", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: approved));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["preapproved"] = "yes", ["request"] = "do it" });

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["approval"].State);
        Assert.Equal("approve", (string?)coordinator.Instances["approval"].Output!["answer"]!);

        var answered = coordinator.Questions.Single();

        Assert.Equal("approve", answered.Given.Single().Answer);
        Assert.Equal("input", answered.Given.Single().By);
        Assert.Equal("preapproved = 'yes'", answered.Given.Single().Input);

        // A quote that stands in the input holds: the person's own words, white space runs as one space - and the run
        // goes on with the answer as it did with the first entry's.
        coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["preapproved"] = "maybe", ["request"] = "Please   Build it without waiting\n for me to approve the plan. Thanks." });

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);

        var quoted = coordinator.Questions.Single();

        Assert.Equal("approve", quoted.Given.Single().Answer);
        Assert.Equal("input", quoted.Given.Single().By);
        Assert.Contains("in input 'request'", quoted.Given.Single().Input, StringComparison.Ordinal);
        Assert.Contains("Build it without waiting for me to approve the plan.", quoted.Given.Single().Input, StringComparison.Ordinal);

        // Reported words the input does not hold answer nothing: the person is asked.
        coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["preapproved"] = "maybe", ["request"] = "something else entirely" });

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(coordinator.OpenQuestions.Single().Given);
    }

    /// <summary>
    /// The repository's bar for a recommendation is read by <c>{recommendations}</c> wherever a template may name it:
    /// the file the orchestrator names, or its folder's own <c>recommendations.md</c> - and where neither is, a
    /// sentence that says so, for a recommendation then says it follows none.
    /// </summary>
    [Fact]
    public async Task TheRepositorysBarForARecommendation_IsReadByItsName()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: approval
                type: pause
                question: '{recommendations}'
                options: [approve, revise]
                text: [revise]
            """);

        // A run held in memory reads no files: no bar is there, and the placeholder says so, naming the orchestrator.
        var coordinator = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(
            "(orchestrator 'flow' has no recommendations file: no bar was written for this repository)",
            coordinator.OpenQuestions.Single().Problem);
    }
}
