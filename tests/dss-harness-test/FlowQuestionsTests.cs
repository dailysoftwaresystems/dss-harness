using System.Text.Json.Nodes;
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

    /// <summary>A fold family step's dry run, as its runner reports it over the verbs' own facts.</summary>
    private static FlowStepResult Dry(RequestStanding standing, ConsentKind[] consents, string[] named, RefusalKind[] refusals, string summary)
        => FlowStepResult.Standing(new FlowDryRun(standing, consents, named, refusals, summary));

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
            FlowStepResult.AtLimit("turns", "12 turns"),
            FlowStepResult.AtLimit("context", "the conversation filled it"),
            FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var first = coordinator.OpenQuestions.Single();
        Assert.Equal("turnLimit", first.Kind);
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

        // abort stands among no options, and is taken all the same: it ends the run as a hard stop does - stopped.
        Assert.True(aborting.TryAnswer(aborting.OpenQuestions.Single().Id, "abort", out _));

        end = await aborting.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
        Assert.Equal("stopped", end.Summary);
        var approval = aborting.Instances["approval"];
        Assert.Equal(FlowInstanceState.Failed, approval.State);
        Assert.Equal(HarnessExit.CommandFailed, approval.Exit);
        Assert.Equal("the run was aborted by the answer to its question", approval.Error);
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
}
