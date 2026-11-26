using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// Pre-existing issues, in memory and on disk over fake step runners: each issue a reason step's answer reports
/// raises one instance of the triage step, never scheduled; the triage's answer is acted on - fix given to the
/// recovery, list added proven to the run's own file, duplicate and unproven recorded and not listed - and
/// <c>{run.preExisting}</c> says what became of each.
/// </summary>
public sealed class FlowPreExistingTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A runner that answers each step as its test scripted it, seeing the issue a raised instance triages.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, with the issue it was raised for, where one did.</summary>
        public List<(string Instance, string? Issue)> Started { get; } = [];

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => Task.FromResult(result);
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by how often it has run.</summary>
        public FakeRunner On(string step, Func<int, FlowStepResult> script)
        {
            var asked = 0;
            _scripts[step] = _ => Task.FromResult(script(++asked));
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by the issue its instance triages.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = run => Task.FromResult(script(run));
            return this;
        }

        public FakeRunner OnAsync(string step, Func<FlowStepRun, Task<FlowStepResult>> script)
        {
            _scripts[step] = script;
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it.</summary>
        public void Hold(string step)
        {
            var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _scripts[step] = _ => gate.Task;
            _gates[step] = gate;
        }

        /// <summary>Completes a held <paramref name="step"/> as passed.</summary>
        public bool Release(string step)
            => _gates[step].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));

        /// <summary>Completes a held <paramref name="step"/> with <paramref name="result"/>.</summary>
        public bool Release(string step, FlowStepResult result)
            => _gates[step].TrySetResult(result);

        /// <summary>Completes a held <paramref name="step"/> as passed once <paramref name="when"/> completes.</summary>
        public void ReleaseWhen(string step, TaskCompletionSource when)
        {
            _ = when.Task.ContinueWith(
                _ => _gates[step].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)),
                TaskScheduler.Default);
        }

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add((run.Instance, run.Issue?.Title));

            // A held step hears the stop, as a machine step's runner does: one a stop or a pause that says all cancels
            // ends there, and is not left to run out the stop's drain bound.
            return await _scripts.GetValueOrDefault(run.Step.Name, _ => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run)
                .WaitAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>
    /// The run's records as a test watches them: each write passed on where the run keeps records, the last snapshot
    /// kept, and a signal once a snapshot holds what the test waits for - the one it was made with, and each it asks
    /// for later.
    /// </summary>
    private sealed class Watched(IFlowRecords? inner, Func<FlowSnapshot, bool> waitsFor) : IFlowRecords
    {
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(Func<FlowSnapshot, bool> Holds, TaskCompletionSource Seen)> _later = [];
        private readonly Lock _gate = new();
        private FlowSnapshot? _last;

        /// <summary>Completes once a snapshot written holds what the test waits for.</summary>
        public Task Seen => _seen.Task;

        /// <summary>The last snapshot written; null before the first.</summary>
        public FlowSnapshot? Last
        {
            get
            {
                lock (_gate)
                {
                    return _last;
                }
            }
        }

        /// <summary>Completes once a snapshot written holds <paramref name="holds"/> - the last one already, or a later one.</summary>
        public Task Until(Func<FlowSnapshot, bool> holds)
        {
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_gate)
            {
                if (_last is { } last && holds(last))
                {
                    _ = seen.TrySetResult();
                }
                else
                {
                    _later.Add((holds, seen));
                }
            }

            return seen.Task;
        }

        /// <inheritdoc/>
        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            inner?.Write(kind, snapshot, instance, from, to, attempt, detail);

            if (waitsFor(snapshot))
            {
                _ = _seen.TrySetResult();
            }

            lock (_gate)
            {
                _last = snapshot;

                foreach (var met in _later.Where(wait => wait.Holds(snapshot)).ToList())
                {
                    _ = met.Seen.TrySetResult();
                    _ = _later.Remove(met);
                }
            }
        }

        /// <inheritdoc/>
        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
            => inner?.Question(questions, refused);

        public void Updates(FlowUpdatesFile updates)
            => inner?.Updates(updates);

        public void AffectedTests(IReadOnlyList<FlowAffectedTests> entries)
            => inner?.AffectedTests(entries);
    }

    /// <summary>Records that keep each detail the run's changes say, with the instance it is said of.</summary>
    private sealed class Said : IFlowRecords
    {
        public List<(string? Instance, string Detail)> Details { get; } = [];

        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            if (detail is not null)
            {
                lock (Details)
                {
                    Details.Add((instance, detail));
                }
            }
        }

        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
        {
        }

        public void Updates(FlowUpdatesFile updates)
        {
        }

        public void AffectedTests(IReadOnlyList<FlowAffectedTests> entries)
        {
        }
    }

    /// <summary>Whether both triage instances of a run had ended done when <paramref name="snapshot"/> was written.</summary>
    private static bool BothTriaged(FlowSnapshot snapshot)
        => snapshot.Steps.Count(step => step.Step == "triage" && step.State == FlowInstanceState.Done) == 2;

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>A step's output, read from JSON text.</summary>
    private static JsonNode? Output(string json)
        => JsonNode.Parse(json);

    /// <summary>One issue a reason step reports, as a test writes it.</summary>
    private static FlowPreExistingReport Issue(string title, bool blocks = false)
        => new(title, "what it is", "src/Parser.cs", "read the file and saw it", blocks);

    /// <summary>The triage's answer for one course, the issue where <paramref name="where"/> says - in the parser's file where it says nowhere.</summary>
    private static FlowStepResult Course(string course, string title, params string[] where)
    {
        var answer = new JsonObject
        {
            ["course"] = course,
            ["title"] = title,
            ["description"] = "what it is",
            ["why"] = "not related to the work",
            ["proof"] = new JsonObject
            {
                ["kind"] = "read",
                ["at"] = "abc123",
                ["file"] = "src/Parser.cs",
                ["lines"] = "10-14",
                ["quote"] = "the broken thing",
            },
        };
        answer["where"] = new JsonArray([.. (where.Length == 0 ? ["src/Parser.cs"] : where).Select(path => (JsonNode)path)]);

        return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: answer);
    }

    /// <summary>The triage's answer for <paramref name="course"/> that names, in <paramref name="field"/>, the issue <paramref name="id"/> - the one it repeats, or the one whose root cause it shares.</summary>
    private static FlowStepResult Naming(string course, string title, string field, string id, params string[] where)
    {
        var answer = Course(course, title, where);
        answer.Output![field] = id;

        return answer;
    }

    /// <summary>The triage's answer that joins its issue - where <paramref name="where"/> says - to the one <paramref name="id"/> names, whose root cause it shares.</summary>
    private static FlowStepResult Joins(string title, string id, params string[] where)
        => Naming("join", title, "joins", id, where);

    /// <summary>
    /// A flow whose recovery diagnoses before it mends, the diagnosis reading the issues joined to the one it fixes and,
    /// for an issue joined to a fix that was diagnosing, what that fix did; an always step asks what became of each issue.
    /// </summary>
    private const string OneCause = """
        name: flow
        recover:
          onSuccess: failed-step
          onFailure: pause
          steps:
            - name: diagnose
              type: reason
              prompt: diagnose.md
              outputs: diagnosis.json
            - name: mend
              type: orient
              dependsOn: [diagnose]
        steps:
          - name: think
            type: reason
            prompt: work.md
            outputs: work.json
            onPreExisting: triage
          - name: tell
            type: pause
            always: true
            question: 'what was there before: {run.preExisting}'
            options: [ok, no]
          - name: triage
            type: reason
            prompt: triage.md
            outputs: pre-existing.json
            raised: preExisting
            onAddress: recover
        """;

    /// <summary>The prompts of <see cref="OneCause"/>, as its workflow's files hold them.</summary>
    private static string OneCausePrompt(string named)
        => named switch
        {
            "work.md" => "Do the work.",
            "diagnose.md" => "joined: {source.joined}; after: {source.joinedTo}",
            _ => "Triage it.",
        };

    /// <summary>
    /// Two issues of one root cause take one visit of the recovery: the second's triage joins it to the first, whose
    /// triage has not ended yet, and once that one sends its issue to fix, the visit's diagnosis is given the joined issue
    /// with its id; the visit's end settles both, and {run.preExisting} and the run's counts say each fixed, the joined
    /// one with the issue it joined.
    /// </summary>
    [Fact]
    public async Task TwoIssuesOfOneRootCause_TakeOneVisit_WhichSettlesBoth_AndTheTallyNamesEach()
    {
        var definition = Parse(OneCause);
        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole"), Issue("the second hole")]))
            .OnAsync("triage", async run =>
            {
                if (run.Issue!.Title == "the second hole")
                {
                    return Joins(run.Issue.Title, "triage");
                }

                await joined.Task;
                return Course("fix", run.Issue.Title);
            })
            .On("diagnose", run =>
            {
                lock (prompts)
                {
                    prompts[run.Instance] = FlowTemplate.Joined(run.Prompts!["prompt"]);
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Instance == "triage+2" && step.State == FlowInstanceState.Done));
        _ = watched.Seen.ContinueWith(_ => joined.TrySetResult(), TaskScheduler.Default);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched, workflowFile: (_, named) => OneCausePrompt(named));
        var cancel = TestContext.Current.CancellationToken;

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(["recover"], coordinator.Instances.Keys.Where(id => id.StartsWith("recover", StringComparison.Ordinal) && !id.Contains('.', StringComparison.Ordinal)));
        Assert.Equal(
            """joined: [{"id":"triage+2","title":"the second hole","description":"what it is","where":"src/Parser.cs","proof":"read the file and saw it"}]; after: """,
            prompts["recover.diagnose"]);
        // In the order the issues were taken: the join's triage ended first.
        Assert.Equal(
            "what was there before: triage+2: fixed with triage - the second hole (src/Parser.cs); triage: fixed - the first hole (src/Parser.cs)",
            coordinator.OpenQuestions.Single(question => question.Kind == "pause").Problem);
        Assert.Equal(new WorkflowPreExisting(0, 2, 0, 0), watched.Last!.PreExisting);
    }

    /// <summary>
    /// An issue joined to one whose triage then lists it - it ends without a fix - is triaged again on its own: a triage
    /// of its own is raised for it, the run's records saying why, told the issues the run holds with the join left out,
    /// and what that triage answers is what becomes of it.
    /// </summary>
    [Fact]
    public async Task AnIssueJoinedToOneThatEndsWithoutAFix_IsTriagedAgainOnItsOwn()
    {
        var definition = Parse(OneCause);
        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole"), Issue("the second hole")]))
            .OnAsync("triage", async run =>
            {
                lock (prompts)
                {
                    prompts[run.Instance] = FlowTemplate.Joined(run.Prompts!["prompt"]);
                }

                switch (run.Instance)
                {
                    case "triage+2":
                        return Joins(run.Issue!.Title, "triage");
                    case "triage":
                        await joined.Task;
                        return Course("list", run.Issue!.Title);
                    default:
                        return Course("list", run.Issue!.Title);
                }
            });
        var said = new Said();
        var watched = new Watched(said, snapshot => snapshot.Steps.Any(step => step.Instance == "triage+2" && step.State == FlowInstanceState.Done));
        _ = watched.Seen.ContinueWith(_ => joined.TrySetResult(), TaskScheduler.Default);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched, workflowFile: (_, named) => OneCausePrompt(named));
        var cancel = TestContext.Current.CancellationToken;

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal([("think", null), ("triage", "the first hole"), ("triage+2", "the second hole"), ("triage+3", "the second hole")], runner.Started);
        Assert.Contains(("triage+2", "the issue 'the second hole' is triaged again on its own: 'triage', whose root cause it joined, ended without a fix"), said.Details);
        Assert.Equal(
            "Triage it.\n\n<pre-existing>\nThe issues that were there before this run which it has taken already, yours left out, each with its id, what became of it, its title and where it is:\n\n"
                + "- pe-1: listed - the first hole (src/Parser.cs)\n\n"
                + "Where yours is one of these, the course is duplicate, and duplicates names its id. No issue taken before yours is being triaged or being fixed, so yours joins none.\n</pre-existing>\n",
            prompts["triage+3"]);
        Assert.Equal(
            "what was there before: pe-1: listed - the first hole (src/Parser.cs); pe-2: listed - the second hole (src/Parser.cs)",
            coordinator.OpenQuestions.Single(question => question.Kind == "pause").Problem);
        Assert.DoesNotContain("recover", coordinator.Instances.Keys);
    }

    /// <summary>
    /// An issue joined to one whose visit is diagnosing already goes with no visit begun before it joined: it waits for
    /// that visit to end, and then takes a visit of its own, its diagnosis told what that visit fixed - the issue it
    /// joined, by its id, the visit and the remedy items it built - so the second fix starts from the tree the first left;
    /// the first visit's diagnosis was given no joined issue, and each is fixed on its own.
    /// </summary>
    [Fact]
    public async Task AnIssueJoinedWhileTheVisitItJoinsIsDiagnosing_TakesAVisitOfItsOwnOnceThatOneEnds_ToldWhatItFixed()
    {
        var definition = Parse(OneCause);
        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        var diagnosing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole"), Issue("the second hole")]))
            .OnAsync("triage", async run =>
            {
                if (run.Issue!.Title == "the first hole")
                {
                    return Course("fix", run.Issue.Title);
                }

                await diagnosing.Task;
                return Joins(run.Issue.Title, "triage");
            })
            .OnAsync("diagnose", async run =>
            {
                lock (prompts)
                {
                    prompts[run.Instance] = FlowTemplate.Joined(run.Prompts!["prompt"]);
                }

                if (run.Instance == "recover.diagnose")
                {
                    diagnosing.TrySetResult();
                    await joined.Task;
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        var said = new Said();
        var watched = new Watched(said, snapshot => snapshot.Steps.Any(step => step.Instance == "triage+2" && step.State == FlowInstanceState.Done));
        _ = watched.Seen.ContinueWith(_ => joined.TrySetResult(), TaskScheduler.Default);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched, workflowFile: (_, named) => OneCausePrompt(named));
        var cancel = TestContext.Current.CancellationToken;

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(
            [("think", null), ("triage", "the first hole"), ("triage+2", "the second hole"), ("recover.diagnose", null), ("recover.mend", null), ("recover+2.diagnose", null), ("recover+2.mend", null)],
            runner.Started);
        Assert.Equal("joined: []; after: ", prompts["recover.diagnose"]);
        Assert.Equal(
            """joined: []; after: {"id":"triage","title":"the first hole","where":"src/Parser.cs","visit":"recover","items":[]}""",
            prompts["recover+2.diagnose"]);
        Assert.Contains(("triage+2", "the issue 'the second hole' is given a visit of its own: 'triage', whose root cause it joined, was fixed by a visit whose diagnosis began before it joined"), said.Details);
        Assert.Equal(
            "what was there before: triage: fixed - the first hole (src/Parser.cs); triage+2: fixed - the second hole (src/Parser.cs)",
            coordinator.OpenQuestions.Single(question => question.Kind == "pause").Problem);
    }

    /// <summary>
    /// A step an issue joined to another's fix stops runs again once that fix is done, sent back by the visit it went
    /// with - though the issue it joined stops nothing, and its own reporter stays as it ended.
    /// </summary>
    [Fact]
    public async Task AStepAJoinedIssueStops_RunsAgainOnceTheVisitItWentWithIsDone()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: first
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: second
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
                dependsOn: [first]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("first", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole")]))
            .On("second", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the second hole", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .OnAsync("triage", async run =>
            {
                if (run.Issue!.Title == "the second hole")
                {
                    return Joins(run.Issue.Title, "triage");
                }

                await joined.Task;
                return Course("fix", run.Issue.Title);
            });
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Instance == "triage+2" && step.State == FlowInstanceState.Done));
        _ = watched.Seen.ContinueWith(_ => joined.TrySetResult(), TaskScheduler.Default);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("recover", coordinator.Instances["second"].LeftBy);
        Assert.Equal(1, runner.Started.Count(started => started.Instance == "second+2"));
        Assert.Null(coordinator.Instances["first"].LeftBy);
        Assert.False(coordinator.Instances.ContainsKey("recover+2"));
    }

    /// <summary>
    /// A triage names, as the issue its own repeats or the one whose root cause it shares, only an issue taken before its
    /// own - so no two ever name each other and both vanish - and joins only an issue the run has taken: an answer that
    /// names one taken after it, itself, or none the run holds fails the triage step, saying so.
    /// </summary>
    [Theory]
    [InlineData("join", "joins", "triage+2", "its answer's joins names 'triage+2', which was not taken before its own issue: an issue names only one taken before it")]
    [InlineData("duplicate", "duplicates", "triage+2", "its answer's duplicates names 'triage+2', which was not taken before its own issue: an issue names only one taken before it")]
    [InlineData("join", "joins", "triage", "its answer's joins names 'triage', which was not taken before its own issue: an issue names only one taken before it")]
    [InlineData("join", "joins", "pe-7", "its answer joins 'pe-7', which is no issue the run has taken: joins names the id of one being triaged or being fixed")]
    [InlineData("join", "joins", "", "its answer joins no issue: joins names the id of the one whose root cause its own shares")]
    public async Task ATriageNamingAnIssueItMayNot_FailsTheTriageStep(string course, string field, string named, string error)
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
                onFailure: continue
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole"), Issue("the second hole")]))
            .On("triage", run => run.Instance == "triage" ? Naming(course, run.Issue!.Title, field, named) : Course("list", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var triage = coordinator.Instances["triage"];
        Assert.Equal(FlowInstanceState.Failed, triage.State);
        Assert.Equal(HarnessExit.ConfigInvalid, triage.Exit);
        Assert.Equal(error, triage.Error);
    }

    /// <summary>
    /// A triage that began after the issue it would join had ended without a fix may not join it - it was never being
    /// triaged or being fixed while this one looked - so an issue triaged again on its own never joins the same fix twice.
    /// </summary>
    [Fact]
    public async Task AJoinToAnIssueThatEndedWithoutAFixBeforeItBegan_FailsTheTriageStep()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 1
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
                onFailure: continue
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole"), Issue("the second hole")]))
            .On("triage", run => run.Instance == "triage" ? Course("list", run.Issue!.Title) : Joins(run.Issue!.Title, "triage"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var joining = coordinator.Instances["triage+2"];
        Assert.Equal(FlowInstanceState.Failed, joining.State);
        Assert.Equal(HarnessExit.ConfigInvalid, joining.Exit);
        Assert.Equal("its answer joins 'triage', which had ended without a fix before it began: an issue joins only one being triaged or being fixed", joining.Error);
    }

    /// <summary>Each issue a reason step's answer reports raises one instance of the triage step, which is never scheduled.</summary>
    [Fact]
    public async Task EachIssueAReasonStepReports_RaisesOneInstanceOfTheTriageStep()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("a stale document")]))
            .On("triage", run => Course("unproven", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // One instance of the triage step for each issue, each given its own - a new visit for each - and the
        // schedule never started it.
        Assert.Equal([("think", null), ("triage", "a broken test"), ("triage+2", "a stale document")], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(2, coordinator.Instances.Values.Count(instance => instance.Step.Name == "triage"));
    }

    /// <summary>
    /// Issues a step reports while a failure's stop is ending the run are not dropped: nothing is started for them then,
    /// and the next session raises their triage.
    /// </summary>
    [Fact]
    public async Task IssuesReportedWhileTheRunStops_AreOwedToTheNextSession()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: broke
                type: ci-read
                onFailure: stop
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);

        // Both end at once, in declaration order: the failure has stopped the run when the issue is read.
        var runner = new FakeRunner()
            .On("broke", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"))
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", run => Course("unproven", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal([("broke", null), ("think", null)], runner.Started);

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The next session raises the triage the issue is owed - beside the failure that ended the run, which it runs
        // again as a new attempt.
        Assert.Equal([("broke", null), ("think", null), ("broke", null), ("triage", "a broken test")], runner.Started);
    }

    /// <summary>An issue the triage answers list for is added to the run's own file, proven, and {run.preExisting} says what became of each.</summary>
    [Fact]
    public async Task AnIssueAnsweredListIsAddedProven_AndRunPreExistingSaysIt()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 4
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: later
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
                dependsOn: [think]
              - name: tell
                type: pause
                dependsOn: [later]
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("a stale document")]));
        runner.Hold("later");
        runner.On("triage", run => Course(run.Issue!.Title == "a broken test" ? "list" : "unproven", run.Issue.Title));

        // Both issues' answers settle the tally: what waited on the reporter goes on only once the run has recorded
        // both triages ended - its own loop reads the release after them - so the run's words are settled when read.
        var watched = new Watched(null, BothTriaged);
        _ = watched.Seen.ContinueWith(_ => runner.Release("later"), TaskScheduler.Default);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);

        // The listed one is in the run's words; the unproven one is never shown as a fact, only named as unproven.
        var asked = coordinator.OpenQuestions.Single(question => question.Kind == "pause");
        Assert.Equal("what was there before: pe-1: listed - a broken test (src/Parser.cs); triage+2: unproven - a stale document", asked.Problem);
    }

    /// <summary>An issue the triage answers fix for is given to the recovery, and where it blocks the reporting step that step runs again.</summary>
    [Fact]
    public async Task AnIssueAnsweredFix_GoesToTheRecovery_AndTheBlockingReporterRunsAgain()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
                  when: "issue.title == 'a hole in the parser' && issue.blocks"
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("triage", run => Course("fix", run.Issue!.Title))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The issue went to the recovery, which read it as {issue}; its work done, the reporting step - whose work
        // the issue blocked - runs again as a new visit, and the run ends done.
        Assert.Equal([("think", null), ("triage", "a hole in the parser"), ("recover.mend", null), ("think+2", null)], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["think+2"].State);
    }

    /// <summary>The recovery's steps read the issue they fix in a template as in an expression: a question reading {issue.title} is filled with it.</summary>
    [Fact]
    public async Task TheRecoverysSteps_ReadTheIssueTheyFix_InATemplate()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: confirm
                  type: pause
                  question: 'fix {issue.title} in {source.step}?'
                  options: [yes]
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser")]))
            .On("triage", run => Course("fix", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = Assert.Single(coordinator.OpenQuestions);
        Assert.Equal("fix a hole in the parser in triage?", asked.Problem);
    }

    /// <summary>An issue that blocks nothing leaves the reporting step as it ended: the run simply goes on once the fix is done.</summary>
    [Fact]
    public async Task AnIssueThatBlocksNothing_LeavesTheReporterAsItEnded()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser")]))
            .On("triage", run => Course("fix", run.Issue!.Title))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal([("think", null), ("triage", "a hole in the parser"), ("recover.mend", null)], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.False(coordinator.Instances.ContainsKey("think+2"));
    }

    /// <summary>
    /// Two issues triages sent to fix at once are the recovery's at once, each in a visit of its own beside the other:
    /// neither waits for the other's visit to end, for only an issue a step of a visit reported waits for that visit.
    /// </summary>
    [Fact]
    public async Task IssuesSentToFixAtOnce_AreFixedSideBySide()
    {
        var definition = Parse(TwoReporters);
        var runner = new FakeRunner()
            .On("first", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole")]))
            .On("second", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the second hole")]))
            .On("triage", run => Course("fix", run.Issue!.Title));
        runner.Hold("mend");
        var watched = new Watched(null, snapshot => BothTriaged(snapshot)
            && snapshot.Steps.Any(step => step.Instance == "recover.mend" && step.State == FlowInstanceState.Running));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);
        await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        // Both visits work while the first's mend is held: the second issue was taken the moment its triage ended.
        Assert.Equal(FlowInstanceState.Running, coordinator.Instances["recover"].State);
        Assert.Equal(FlowInstanceState.Running, coordinator.Instances["recover+2"].State);
        Assert.Equal(
            [("first", null), ("second", null), ("triage", "the first hole"), ("triage+2", "the second hole"), ("recover.mend", null), ("recover+2.mend", null)],
            runner.Started);

        _ = runner.Release("mend");
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [("first", null), ("second", null), ("triage", "the first hole"), ("triage+2", "the second hole"), ("recover.mend", null), ("recover+2.mend", null)],
            runner.Started);
    }

    /// <summary>
    /// Visits side by side stay inside the run's own limits: every step of every visit counts against the pools the
    /// scheduler applies to any step - here one reasoning agent at a time - so the second visit's diagnosis waits for
    /// room, its record saying so, and never runs beside the first; both visits then finish.
    /// </summary>
    [Fact]
    public async Task VisitsSideBySide_CountAgainstTheRunsLimits()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 1
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: reason
                  prompt: diagnose.md
                  outputs: diagnosis.json
                - name: mend
                  type: orient
                  dependsOn: [diagnose]
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var diagnosing = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (running, most) = (0, 0);
        var counting = new Lock();
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole"), Issue("the second hole")]))
            .On("triage", run => Course("fix", run.Issue!.Title))
            .OnAsync("diagnose", async run =>
            {
                lock (counting)
                {
                    most = Math.Max(most, ++running);
                }

                try
                {
                    return run.Instance == "recover.diagnose" ? await diagnosing.Task : FlowStepResult.Ended(passed: true, HarnessExit.Success);
                }
                finally
                {
                    lock (counting)
                    {
                        running--;
                    }
                }
            });
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Instance == "recover.diagnose" && step.State == FlowInstanceState.Running)
            && snapshot.Steps.Any(step => step.Instance == "recover+2.diagnose" && step.State == FlowInstanceState.NotStarted && step.WaitsOn.Count > 0));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);
        await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        // Both visits are made, side by side; the second's diagnosis waits for the one reasoning agent the run allows.
        Assert.Equal(FlowInstanceState.Running, coordinator.Instances["recover"].State);
        Assert.Equal(FlowInstanceState.Running, coordinator.Instances["recover+2"].State);
        Assert.Equal([FlowWaits.ReasoningAgents], watched.Last!.Steps.Single(step => step.Instance == "recover+2.diagnose").WaitsOn);

        _ = diagnosing.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(1, most);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["recover"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["recover+2"].State);
        Assert.Equal(
            [("think", null), ("triage", "the first hole"), ("triage+2", "the second hole"), ("recover.diagnose", null), ("recover+2.diagnose", null), ("recover.mend", null), ("recover+2.mend", null)],
            runner.Started);
    }

    /// <summary>
    /// An issue a step of a visit of the recovery reported waits for that visit to end before the recovery takes it, the
    /// run's records saying so: the recovery never calls itself. Once the visit has ended, the issue's own visit is made.
    /// </summary>
    [Fact]
    public async Task AnIssueAStepOfARecoveryVisitReported_WaitsForThatVisitToEnd()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: reason
                  prompt: diagnose.md
                  outputs: diagnosis.json
                  onPreExisting: triage
                - name: mend
                  type: orient
                  dependsOn: [diagnose]
            steps:
              - name: broke
                type: orient
                onFailure: recover
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var mending = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("broke", asked => asked == 1 ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "it broke") : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("diagnose", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole the diagnosis met")])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("triage", run => Course("fix", run.Issue!.Title))
            .OnAsync("mend", run => run.Instance == "recover.mend" ? mending.Task : Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)));
        var said = new Said();
        var watched = new Watched(said, snapshot => snapshot.Steps.Any(step => step.Instance == "triage" && step.State == FlowInstanceState.Done)
            && snapshot.Steps.Any(step => step.Instance == "recover.mend" && step.State == FlowInstanceState.Running));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);
        await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        // The visit whose diagnosis reported the issue works on; the issue waits for it, and no visit of its own is made.
        Assert.False(coordinator.Instances.ContainsKey("recover+2"));
        Assert.Equal([("broke", null), ("recover.diagnose", null), ("triage", "a hole the diagnosis met"), ("recover.mend", null)], runner.Started);
        Assert.Contains(said.Details, said => said.Instance == "triage"
            && said.Detail == "the issue 'a hole the diagnosis met' waits for 'recover' to end, a step of which reported it: the recovery never calls itself");

        _ = mending.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The visit ended done: the step it took runs again, and the issue's own visit is made and does its work.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Same(coordinator.Instances["triage"], coordinator.Instances["recover+2"].RaisedBy);
        Assert.Equal(
            [("broke+2", null), ("recover+2.diagnose", null), ("recover+2.mend", null)],
            runner.Started.Skip(4).OrderBy(started => started.Instance, StringComparer.Ordinal));
    }

    /// <summary>
    /// The one issue sent to fix for two steps it stopped is fixed once at a time: the second fix waits for the visit at
    /// work on it, the run's records saying so, and is diagnosed on the tree the first fix left - two visits side by side
    /// would build the same lines twice. Each step runs again once its own fix is done.
    /// </summary>
    [Fact]
    public async Task TheOneIssueSentToFixForTwoSteps_WaitsForTheVisitAtWorkOnIt()
    {
        var definition = Parse(TwoReporters);
        FlowStepResult Blocked(int asked)
            => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success);
        var mending = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("first", Blocked)
            .On("second", Blocked)
            .On("triage", run => Course("fix", run.Issue!.Title))
            .OnAsync("mend", run => run.Instance == "recover.mend" ? mending.Task : Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)));
        var said = new Said();
        var watched = new Watched(said, snapshot => BothTriaged(snapshot)
            && snapshot.Steps.Any(step => step.Instance == "recover.mend" && step.State == FlowInstanceState.Running));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);
        await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        Assert.False(coordinator.Instances.ContainsKey("recover+2"));
        Assert.Contains(said.Details, said => said.Instance == "triage+2"
            && said.Detail == "the issue 'a hole in the parser' waits for 'recover' to end, which fixes it already for another step it stopped");

        _ = mending.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The first fix done, the second is taken; each step its fix was for runs again.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Same(coordinator.Instances["triage+2"], coordinator.Instances["recover+2"].RaisedBy);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["first+2"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["second+2"].State);
    }

    /// <summary>
    /// A step two issues stop runs again once, after every visit for an issue that stops it has ended: never while one
    /// of them still works, and never a second time when the second ends.
    /// </summary>
    [Fact]
    public async Task AStepTwoIssuesStop_RunsAgainOnce_AfterBothFixesEnded()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var mending = new Dictionary<string, TaskCompletionSource<FlowStepResult>>(StringComparer.Ordinal)
        {
            ["recover.mend"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["recover+2.mend"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var runner = new FakeRunner()
            .On("think", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole", blocks: true), Issue("the second hole", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("triage", run => Course("fix", run.Issue!.Title))
            .OnAsync("mend", run => mending[run.Instance].Task);
        var watched = new Watched(null, snapshot => new[] { "recover.mend", "recover+2.mend" }.All(mend => snapshot.Steps.Any(step => step.Instance == mend && step.State == FlowInstanceState.Running)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);
        await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        _ = mending["recover.mend"].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        await watched.Until(snapshot => snapshot.Steps.Any(step => step.Instance == "recover" && step.State == FlowInstanceState.Done)).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        // The first fix is done and the second still works: the step they stop is not sent back yet.
        Assert.False(coordinator.Instances.ContainsKey("think+2"));
        Assert.Null(coordinator.Instances["think"].LeftBy);

        _ = mending["recover+2.mend"].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // Both ended: the step ran again once, sent back by the visit that ended last, and no more.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [("think", null), ("triage", "the first hole"), ("triage+2", "the second hole"), ("recover.mend", null), ("recover+2.mend", null), ("think+2", null)],
            runner.Started);
        Assert.Equal("recover+2", coordinator.Instances["think"].LeftBy);
        Assert.False(coordinator.Instances.ContainsKey("think+3"));
    }

    /// <summary>
    /// A step two issues stop, the first fixed while the second is still being triaged, runs again once the second is
    /// settled - here listed, so no visit is made for it: the fix done waits for every issue of the step to settle, and
    /// the triage that settles the last sends the step back, once.
    /// </summary>
    [Fact]
    public async Task AStepWhoseOtherIssueIsSettledAfterAFix_RunsAgainOnceThatIssueIsSettled()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var triaging = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("think", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole", blocks: true), Issue("the second hole", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .OnAsync("triage", run => run.Issue!.Title == "the second hole" ? triaging.Task : Task.FromResult(Course("fix", run.Issue.Title)));
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Instance == "recover" && step.State == FlowInstanceState.Done));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);
        await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        // The first issue is fixed, and the second is still being triaged: the step waits for it.
        Assert.False(coordinator.Instances.ContainsKey("think+2"));

        _ = triaging.TrySetResult(Course("list", "the second hole"));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal([("think", null), ("triage", "the first hole"), ("triage+2", "the second hole"), ("recover.mend", null), ("think+2", null)], runner.Started);
        Assert.Equal("recover", coordinator.Instances["think"].LeftBy);
        Assert.False(coordinator.Instances.ContainsKey("think+3"));
    }

    /// <summary>
    /// A flow whose work items each own the paths their plan gives them, beside the recovery: the plan waits for the test
    /// to let it go, the recovery's diagnosis gives a remedy item its check reads, its mend claims no tree - so an item's
    /// making of its agent, which takes the main tree, waits for nothing of it - and the <c>amend</c> block the
    /// definition is given names the items' three alternatives, which makes <c>items</c> the work-item group.
    /// </summary>
    private const string ItemsBesideAFix = """
        name: flow
        maxOpenAgents: 3
        inputs:
          - name: request
            default: ''
        recover:
          onSuccess: failed-step
          onFailure: pause
          steps:
            - name: diagnose
              type: orient
            - name: remedy-check
              type: validate-plan
              items: 'diagnose.items'
              dependsOn: [diagnose]
            - name: mend
              type: ci-read
              dependsOn: [remedy-check]
        steps:
          - name: think
            type: reason
            prompt: work.md
            outputs: work.json
            onPreExisting: triage
          - name: plan
            type: orient
            dependsOn: [think]
          - name: replan
            type: reason
            prompt: plan-update.md
            outputs: plan.json
          - name: items
            dependsOn: [plan]
            forEach: 'plan.items'
            steps:
              - name: make
                type: create-agent
                agent: builder
              - name: build
                type: reason
                agent: builder
                in: make
                dependsOn: [make]
                prompt: build.md
                outputs: out.json
              - name: rebuild
                type: reason
                agent: builder
                in: make
                prompt: rebuild.md
                outputs: out.json
              - name: withdraw
                type: reason
                agent: builder
                in: make
                prompt: withdraw.md
                outputs: out.json
          - name: triage
            type: reason
            prompt: triage.md
            outputs: pre-existing.json
            raised: preExisting
            onAddress: recover
        """;

    /// <summary>A work item as a plan gives it: its name and the paths it owns.</summary>
    private static JsonObject WorkItem(string name, params string[] owned)
        => new() { ["name"] = name, ["ownedPaths"] = new JsonArray([.. owned.Select(path => (JsonNode)path)]) };

    /// <summary>
    /// An issue the recovery is fixing holds the files its triage's where names, and - once its diagnosis is checked - the
    /// paths its remedy items own: a work item that owns any of them waits for the visit to end, its record saying which
    /// fix holds which file, and starts after it, on the tree the fix left; an item that owns none starts at once, never
    /// held behind the one that waits. A command the where names holds no file.
    /// </summary>
    [Fact]
    public async Task AWorkItemOwningAFileAnIssueBeingFixedHolds_StartsOnceTheFixEnded_AndOneOwningNoneStartsAtOnce()
    {
        var definition = Parse(ItemsBesideAFix) with
        {
            Amend = new FlowAmend("request", 5, null, "replan", "plan", "replan.latest.scope", "items.build", "items.rebuild", "items.withdraw", null),
        };
        var planning = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mending = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remedies = new JsonArray(WorkItem("mend-the-c-part", "src/c/remedy.c"));
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser")]))
            .On("triage", run => Course("fix", run.Issue!.Title, "src/a/main.c", "make -C src/b"))
            .On("diagnose", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["items"] = remedies.DeepClone() }))
            .On("remedy-check", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["ok"] = true, ["problems"] = new JsonArray(), ["items"] = remedies.DeepClone() }))
            .OnAsync("plan", _ => planning.Task)
            .OnAsync("mend", _ => mending.Task);
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Instance == "recover.mend" && step.State == FlowInstanceState.Running));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        // The plan stands once the fix is at work, its remedy checked: its items open beside the fix.
        var session = coordinator.RunAsync(cancel);
        await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        _ = planning.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
        {
            ["items"] = new JsonArray(WorkItem("a", "src/a/**"), WorkItem("b", "src/b/**"), WorkItem("c", "src/c/**")),
        }));
        await watched.Until(snapshot => snapshot.Steps.Any(step => step.Instance == "items[1].build" && step.State == FlowInstanceState.Done)).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        // The item that owns neither the file the triage named nor the remedy's went on at once and is built; the two that
        // own one wait, each saying for what.
        Assert.Equal(
            [("think", null), ("plan", null), ("triage", "a hole in the parser"), ("recover.diagnose", null), ("recover.remedy-check", null), ("recover.mend", null), ("items[1].make", null), ("items[1].build", null)],
            runner.Started);
        Assert.Equal(FlowInstanceState.NotStarted, coordinator.Instances["items[0].make"].State);
        Assert.Equal(FlowInstanceState.NotStarted, coordinator.Instances["items[2].make"].State);
        var steps = watched.Last!.Steps;
        Assert.Equal([FlowWaits.Fix("a hole in the parser", "recover", ["src/a/main.c"])], steps.Single(step => step.Instance == "items[0].make").WaitsOn);
        Assert.Equal(["the fix of 'a hole in the parser' (recover), which holds src/c/remedy.c"], steps.Single(step => step.Instance == "items[2].make").WaitsOn);

        _ = mending.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The fix done, the two held items start on the tree it left, and the run ends done.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [("items[0].make", null), ("items[2].make", null), ("items[0].build", null), ("items[2].build", null)],
            runner.Started.Skip(8));
    }

    /// <summary>
    /// An issue joined to another's fix holds the files its own triage's where names while that fix works, as the issue
    /// it joined holds its own: a work item that owns one waits for the visit it goes with, its record naming the joined
    /// issue; one that owns neither issue's file starts at once.
    /// </summary>
    [Fact]
    public async Task AWorkItemOwningAFileAJoinedIssueNames_WaitsForTheFixItWentWith()
    {
        var definition = Parse(ItemsBesideAFix) with
        {
            Amend = new FlowAmend("request", 5, null, "replan", "plan", "replan.latest.scope", "items.build", "items.rebuild", "items.withdraw", null),
        };
        var planning = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mending = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole"), Issue("the second hole")]))
            .OnAsync("triage", async run =>
            {
                if (run.Issue!.Title == "the second hole")
                {
                    return Joins(run.Issue.Title, "triage", "src/b/main.c");
                }

                await joined.Task;
                return Course("fix", run.Issue.Title, "src/a/main.c");
            })
            .On("diagnose", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["items"] = new JsonArray() }))
            .On("remedy-check", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["ok"] = true, ["problems"] = new JsonArray(), ["items"] = new JsonArray() }))
            .OnAsync("plan", _ => planning.Task)
            .OnAsync("mend", _ => mending.Task);
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Instance == "triage+2" && step.State == FlowInstanceState.Done));
        _ = watched.Seen.ContinueWith(_ => joined.TrySetResult(), TaskScheduler.Default);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        // The plan stands once the one fix of both issues is at work.
        var session = coordinator.RunAsync(cancel);
        await watched.Until(snapshot => snapshot.Steps.Any(step => step.Instance == "recover.mend" && step.State == FlowInstanceState.Running)).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        _ = planning.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
        {
            ["items"] = new JsonArray(WorkItem("a", "src/a/**"), WorkItem("b", "src/b/**"), WorkItem("c", "src/c/**")),
        }));
        await watched.Until(snapshot => snapshot.Steps.Any(step => step.Instance == "items[2].build" && step.State == FlowInstanceState.Done)).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        Assert.Equal(FlowInstanceState.NotStarted, coordinator.Instances["items[0].make"].State);
        Assert.Equal(FlowInstanceState.NotStarted, coordinator.Instances["items[1].make"].State);
        var steps = watched.Last!.Steps;
        Assert.Equal([FlowWaits.Fix("the first hole", "recover", ["src/a/main.c"])], steps.Single(step => step.Instance == "items[0].make").WaitsOn);
        Assert.Equal([FlowWaits.Fix("the second hole", "recover", ["src/b/main.c"])], steps.Single(step => step.Instance == "items[1].make").WaitsOn);
        Assert.False(coordinator.Instances.ContainsKey("recover+2"));

        _ = mending.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items[1].build"].State);
    }

    /// <summary>An entry of a triage's where holds a file only where it is a path from the repository's root: never a command, a pattern, or a path outside the tree.</summary>
    [Theory]
    [InlineData("src/a/main.c", true)]
    [InlineData("Makefile", true)]
    [InlineData("src/", true)]
    [InlineData("make -C src/b", false)]
    [InlineData("src/*.c", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("../outside.c", false)]
    [InlineData("", false)]
    public void AnEntryOfWhere_HoldsAFile_OnlyWhereItIsAPathFromTheRoot(string entry, bool path)
        => Assert.Equal(path, FlowIssueHold.IsPath(entry));

    /// <summary>
    /// Fixes side by side are each counted once, in the place their issue was taken: being fixed when its triage ended,
    /// and fixed once the visit that took it ended - so {run.preExisting} says the issues in the order they were taken,
    /// whichever visit ended first.
    /// </summary>
    [Fact]
    public async Task FixesSideBySide_AreEachCountedOnce_InTheOrderTheirIssuesWereTaken()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: first
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: second
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: tell
                type: pause
                always: true
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var mending = new Dictionary<string, TaskCompletionSource<FlowStepResult>>(StringComparer.Ordinal)
        {
            ["recover.mend"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["recover+2.mend"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var runner = new FakeRunner()
            .On("first", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole")]))
            .On("second", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the second hole")]))
            .On("triage", run => Course("fix", run.Issue!.Title))
            .OnAsync("mend", run => mending[run.Instance].Task);
        var watched = new Watched(null, snapshot => new[] { "recover.mend", "recover+2.mend" }.All(mend => snapshot.Steps.Any(step => step.Instance == mend && step.State == FlowInstanceState.Running)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);
        var cancel = TestContext.Current.CancellationToken;

        // The second issue's visit ends first.
        var session = coordinator.RunAsync(cancel);
        await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        _ = mending["recover+2.mend"].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        await watched.Until(snapshot => snapshot.Steps.Any(step => step.Instance == "recover+2" && step.State == FlowInstanceState.Done)).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        _ = mending["recover.mend"].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(
            "what was there before: triage: fixed - the first hole (src/Parser.cs); triage+2: fixed - the second hole (src/Parser.cs)",
            coordinator.OpenQuestions.Single(question => question.Kind == "pause").Problem);
    }

    /// <summary>
    /// An issue the triage answers duplicate for is recorded and not listed, the listed one it repeats named as its
    /// answer's duplicates names it; the run's counts say what became of each.
    /// </summary>
    [Fact]
    public async Task AnIssueAnsweredDuplicate_IsRecordedAndNotListed()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: tell
                type: pause
                always: true
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("the same broken test")]))
            .On("triage", run =>
            {
                if (run.Issue!.Title == "a broken test")
                {
                    return Course("list", run.Issue.Title);
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"course": "duplicate", "title": "the same broken test", "duplicates": "pe-1"}"""));
            });
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();

        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);

            _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // The always step reads the run's words once every triage has answered.
            var asked = coordinator.OpenQuestions.Single(question => question.Kind == "pause");
            Assert.Equal("what was there before: pe-1: listed - a broken test (src/Parser.cs); triage+2: duplicate of pe-1 - the same broken test", asked.Problem);
        }

        // The file holds the one listed issue, proven; the duplicate added nothing.
        var issues = store.ReadPreExistingIssues(layout);

        Assert.NotNull(issues);
        var listed = Assert.Single(issues.Issues);
        Assert.Equal("pe-1", listed.Id);
        Assert.Equal("a broken test", listed.Title);
        Assert.Equal("read", listed.Proof.Kind);
        Assert.Equal("abc123", listed.Proof.At);

        // The run's status counts what became of each.
        var status = store.ReadWorkflowStatus(layout);

        Assert.NotNull(status);
        Assert.Equal(new WorkflowPreExisting(1, 0, 1, 0), status.Progress.PreExisting);
    }

    /// <summary>
    /// An issue the triage answers next for is owed the run's next round, sized - never listed, never given to the
    /// recovery - and <c>{run.preExisting}</c> says so; the run's own file of next-round issues holds it with its size,
    /// and an answer whose size names no file the fix touches is refused, its course never taken.
    /// </summary>
    [Fact]
    public async Task AnIssueAnsweredNext_IsOwedTheRunsNextRound_Sized()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: tell
                type: pause
                always: true
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var answer = (JsonObject)Course("next", "a broken test").Output!.DeepClone();
        answer["size"] = new JsonObject
        {
            ["files"] = new JsonArray("src/Parser.cs"),
            ["build"] = "the leg the parser tests build",
            ["measureFirst"] = new JsonArray("the parser's cells of the universe"),
        };
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: answer));
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();

        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);

            _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var asked = coordinator.OpenQuestions.Single(question => question.Kind == "pause");
            Assert.Equal("what was there before: nx-1: next round - a broken test (src/Parser.cs)", asked.Problem);
        }

        // The run's next round holds the issue, sized, by its id; its list of listed issues holds none.
        var owed = store.ReadNextRound(layout);

        Assert.NotNull(owed);
        var issue = Assert.Single(owed.Issues);
        Assert.Equal("nx-1", issue.Id);
        Assert.Equal("a broken test", issue.Title);
        Assert.Equal(["src/Parser.cs"], issue.Files);
        Assert.Equal("the leg the parser tests build", issue.Build);
        Assert.Equal(["the parser's cells of the universe"], issue.MeasureFirst);
        Assert.Empty(store.ReadPreExistingIssues(layout)!.Issues);

        var status = store.ReadWorkflowStatus(layout);

        Assert.NotNull(status);
        Assert.Equal(new WorkflowPreExisting(0, 0, 0, 0), status.Progress.PreExisting);
    }

    /// <summary>
    /// An issue the triage answers next for with no file its fix touches - none at all, or one whose name is blank - is
    /// refused, the triage failing: the course sizes the work, a size with no file sizes nothing, and the triage is held to
    /// the rules the run's file of the issues keeps, so it never ends done with an issue the next write could not keep.
    /// </summary>
    /// <param name="files">The files the size names, as JSON.</param>
    [Theory]
    [InlineData("[]")]
    [InlineData("[\" \"]")]
    [InlineData("[\"src/Parser.cs\", \"\"]")]
    public async Task AnIssueAnsweredNext_ByASizeThatNamesNoFile_IsRefused(string files)
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
                onFailure: continue
            """);
        var answer = (JsonObject)Course("next", "a broken test").Output!.DeepClone();
        answer["size"] = new JsonObject
        {
            ["files"] = JsonNode.Parse(files),
            ["build"] = "",
            ["measureFirst"] = new JsonArray(),
        };
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: answer));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var triage = coordinator.Instances["triage"];

        Assert.Equal(FlowInstanceState.Failed, triage.State);
        Assert.Equal(HarnessExit.ConfigInvalid, triage.Exit);
        Assert.Equal(
            "its answer's issue does not read back: its size names no file the fix touches: a next round's item is sized by its files",
            triage.Error);
    }

    /// <summary>
    /// An issue a triage holds already - the same title at the same place, reported again by a later step, its other
    /// words different - raises nothing, and the run's records say which triage holds it: each found issue is triaged
    /// once, and a repeat worded differently stays the triage's to merge by the duplicate course.
    /// </summary>
    [Fact]
    public async Task AnIssueTheRunAlreadyRaisedATriageFor_IsNotRaisedAgain()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: first
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: second
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("first", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("second", FlowStepResult.Ended(passed: true, HarnessExit.Success,
                preExisting:
                [
                    new FlowPreExistingReport("a broken test", "worded differently", "src/Parser.cs", "saw it again", false),
                    Issue("a stale document"),
                ]))
            .On("triage", run => Course("unproven", run.Issue!.Title));
        var said = new Said();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: said);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The re-reported issue raised no second triage, and the run's records say which triage holds it; the new issue
        // raised its one, and the run ends done.
        Assert.Equal([("first", null), ("second", null), ("triage", "a broken test"), ("triage+2", "a stale document")], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(2, coordinator.Instances.Values.Count(instance => instance.Step.Name == "triage"));
        Assert.Contains(("second", "pre-existing issue 'a broken test' (src/Parser.cs) reported again: triage triages it"), said.Details);
    }

    /// <summary>
    /// A step that reports issues is told, at the end of its prompt, every issue the run holds where it starts - each
    /// one's id, what became of it, its title and where it is, a duplicate left out for its issue is there under its own,
    /// and one whose triage has not ended being triaged - so a repeat it would word its own way raises no triage to merge
    /// it; a step the run holds none for is given its prompt as filled. A triage is told the same issues, its own left
    /// out, with the ids of those it may join - each taken before its own, being triaged or being fixed - so it names the
    /// one its issue repeats, or the one whose root cause it shares, by its id.
    /// </summary>
    [Fact]
    public async Task AStepThatReportsIssues_IsToldEveryIssueTheRunHoldsWhereItStarts()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: gate
                type: orient
                dependsOn: [think]
              - name: later
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
                dependsOn: [gate]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        var laterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoTriaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watched = new Watched(null, BothTriaged);
        _ = watched.Seen.ContinueWith(_ => twoTriaged.TrySetResult(), TaskScheduler.Default);
        string PromptOf(FlowStepRun run)
        {
            lock (prompts)
            {
                return prompts[run.Instance] = FlowTemplate.Joined(run.Prompts!["prompt"]);
            }
        }

        var runner = new FakeRunner()
            .On("think", run =>
            {
                _ = PromptOf(run);
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("a stale document"), Issue("a dead link")]);
            })
            .OnAsync("gate", async _ =>
            {
                await twoTriaged.Task;
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            })
            .On("later", run =>
            {
                _ = PromptOf(run);
                laterStarted.TrySetResult();
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            })
            .OnAsync("triage", async run =>
            {
                _ = PromptOf(run);

                switch (run.Issue!.Title)
                {
                    case "a broken test":
                        return Course("list", run.Issue.Title);
                    case "a stale document":
                        await thirdStarted.Task;
                        return Course("duplicate", run.Issue.Title);
                    default:
                        thirdStarted.TrySetResult();
                        await laterStarted.Task;
                        return Course("unproven", run.Issue.Title);
                }
            });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched, workflowFile: (_, named) => named == "work.md" ? "Do the work." : "Triage it.");

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("Do the work.", prompts["think"]);
        Assert.Equal(
            "Do the work.\n\n<pre-existing>\nThe issues that were there before this run which its steps have reported already, each with its id, what became of it, its title and where it is:\n\n"
                + "- pe-1: listed - a broken test (src/Parser.cs)\n- triage+3: being triaged - a dead link (src/Parser.cs)\n\n"
                + "Report none of these again under preExisting, however you would word it, unless it stops your own work: a step of the run holds each one already.\n</pre-existing>\n",
            prompts["later"]);

        // Two reasoning agents at a time: the first two triages start together, each told the two others being triaged,
        // and the third once the first has listed its issue - told that, and the second still being triaged, which it
        // may join, as it may not the listed one.
        string Told(string held, string joins)
            => "Triage it.\n\n<pre-existing>\nThe issues that were there before this run which it has taken already, yours left out, each with its id, what became of it, its title and where it is:\n\n"
                + held
                + "\nWhere yours is one of these, the course is duplicate, and duplicates names its id. "
                + joins
                + "\n</pre-existing>\n";
        Assert.Equal(
            Told("- triage+2: being triaged - a stale document (src/Parser.cs)\n- triage+3: being triaged - a dead link (src/Parser.cs)\n", "No issue taken before yours is being triaged or being fixed, so yours joins none."),
            prompts["triage"]);
        Assert.Equal(
            Told(
                "- triage: being triaged - a broken test (src/Parser.cs)\n- triage+3: being triaged - a dead link (src/Parser.cs)\n",
                "Where yours is another issue whose root cause is that of one taken before it that is being triaged or being fixed - triage - the course is join, and joins names that one's id: its fix is that issue's, which settles both."),
            prompts["triage+2"]);
        Assert.Equal(
            Told(
                "- pe-1: listed - a broken test (src/Parser.cs)\n- triage+2: being triaged - a stale document (src/Parser.cs)\n",
                "Where yours is another issue whose root cause is that of one taken before it that is being triaged or being fixed - triage+2 - the course is join, and joins names that one's id: its fix is that issue's, which settles both."),
            prompts["triage+3"]);
    }

    /// <summary>
    /// The same title at another place is another issue, and raises its own triage: an issue is the same one only where
    /// its title and where it is both are.
    /// </summary>
    [Fact]
    public async Task AnIssueOfTheSameTitleAtAnotherPlace_RaisesItsOwnTriage()
    {
        var definition = Parse(TwoReporters);
        var runner = new FakeRunner()
            .On("first", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a null dereference")]))
            .On("second", FlowStepResult.Ended(passed: true, HarnessExit.Success,
                preExisting: [new FlowPreExistingReport("a null dereference", "what it is", "src/Lexer.cs", "read the file and saw it", false)]))
            .On("triage", run => Course("unproven", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            ["src/Lexer.cs", "src/Parser.cs"],
            coordinator.Instances.Values.Where(instance => instance.Step.Name == "triage").Select(instance => instance.Issue!.Where).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A repeat that stops its reporter's own work is raised for that reporter, whatever triage another step's report
    /// of it raised: only a triage's own reporter runs again once the fix is done, so the step it stopped is not left
    /// with its work undone.
    /// </summary>
    [Fact]
    public async Task ARepeatThatStopsItsReportersWork_RaisesItsOwnTriage_AndTheReporterRunsAgainAfterTheFix()
    {
        var definition = Parse(TwoReporters);
        var runner = new FakeRunner()
            .On("first", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser")]))
            .On("second", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("triage", run => Course(run.Issue!.Blocks ? "fix" : "unproven", run.Issue.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(2, coordinator.Instances.Values.Count(instance => instance.Step.Name == "triage"));
        Assert.Contains(("recover.mend", null), runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["second+2"].State);
    }

    /// <summary>
    /// An issue reported again after its fix was done is one the fix did not hold, and is raised anew: the run's
    /// tally does not say fixed alone of an issue a later step still meets.
    /// </summary>
    [Fact]
    public async Task AnIssueReportedAgainAfterItsFix_IsRaisedAnew()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", asked => FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: asked == 1)]))
            .On("triage", run => Course(run.Issue!.Blocks ? "fix" : "unproven", run.Issue.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The fix done, the reporter ran again and met the issue still: a second triage took it.
        Assert.Equal([("think", null), ("triage", "a hole in the parser"), ("recover.mend", null), ("think+2", null), ("triage+2", "a hole in the parser")], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>
    /// A fix that never holds does not go round for ever: the issue's visits of the recovery are counted against one
    /// bound, whichever triage sent it to fix, and once it is spent the run asks rather than raising the issue again.
    /// </summary>
    [Fact]
    public async Task AFixThatNeverHolds_SpendsTheIssuesOneRecoveryBound_AndTheRunAsks()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              maxVisits: 1
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)]))
            .On("triage", run => Course("fix", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // One visit of the recovery for the issue, its fix not holding; the second triage's fix finds the bound spent.
        Assert.Equal([("think", null), ("triage", "a hole in the parser"), ("recover.mend", null), ("think+2", null), ("triage+2", "a hole in the parser")], runner.Started);
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = coordinator.OpenQuestions.Single(question => question.Kind == "recoveryBound");
        Assert.Contains("'a hole in the parser' is to be fixed at its root, and the recovery has taken it 1 time without putting it right", asked.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Steps the one issue stopped side by side each have it fixed for them, and none spends another's bound: each
    /// reported it before any fix of it was done, so each triage's visit of the recovery is counted for that triage
    /// alone, and with a bound of one visit both are fixed and both run again.
    /// </summary>
    [Fact]
    public async Task StepsTheOneIssueStoppedSideBySide_EachHaveItFixed_WithoutSpendingAnothersBound()
    {
        var definition = Parse(TwoReporters.Replace("onFailure: pause", "onFailure: pause\n  maxVisits: 1", StringComparison.Ordinal));
        FlowStepResult Blocked(int asked)
            => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success);
        var runner = new FakeRunner()
            .On("first", Blocked)
            .On("second", Blocked)
            .On("triage", run => Course("fix", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.DoesNotContain(coordinator.OpenQuestions, question => question.Kind == "recoveryBound");
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["first+2"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["second+2"].State);
    }

    /// <summary>
    /// A configured answer to the recovery's bound is counted where the bound is, not by the triage that asked: a fix
    /// that never holds asks on a new triage each time, and the answer's count runs out all the same - its then ends
    /// the matter rather than the run going round again.
    /// </summary>
    [Fact]
    public async Task AConfiguredAnswerToTheRecoverysBound_RunsOut_AcrossTheTriagesAFixThatNeverHoldsRaises()
    {
        var definition = Parse("""
            name: flow
            answers:
              recoveryBound:
                answer: more
                times: 1
                then: abort
            recover:
              onSuccess: failed-step
              onFailure: pause
              maxVisits: 1
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)]))
            .On("triage", run => Course("fix", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The fix taken once, then once more the configuration gave, then the answer's then: two visits of the
        // recovery's mend and no more.
        Assert.Equal(2, runner.Started.Count(started => started.Instance.EndsWith(".mend", StringComparison.Ordinal)));
        Assert.NotEqual(FlowRunStatus.Done, end.Status);
    }

    /// <summary>
    /// Once a fix did not hold and the issue was triaged anew, the new triage holds it: a later step meeting it again is
    /// merged there and said in the run's records, not triaged once more for every report after the first fix.
    /// </summary>
    [Fact]
    public async Task AnIssueTriagedAnewAfterItsFix_IsHeldByTheNewTriage()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: review
                type: reason
                prompt: work.md
                outputs: work.json
                dependsOn: [think]
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", asked => FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: asked == 1)]))
            .On("review", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser")]))
            .On("triage", run => Course(run.Issue!.Blocks ? "fix" : "list", run.Issue.Title));
        var said = new Said();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: said);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The fix, its reporter met the issue again and a second triage listed it; the review the fix sent the run back
        // past met it after the fix too, and the second triage held it.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["triage", "triage+2"], coordinator.Instances.Values.Where(instance => instance.Step.Name == "triage").Select(instance => instance.Id).Order(StringComparer.Ordinal));
        Assert.Contains(said.Details, said => said.Instance == "review+2" && said.Detail == "pre-existing issue 'a hole in the parser' (src/Parser.cs) reported again: triage+2 triages it");
    }

    /// <summary>
    /// Items of one forEach are steps side by side: an item that met the issue before another item's fix was done has it
    /// fixed on its own bound, whatever that fix sent back in its own item - the visit a fix left counts only for later
    /// visits of the same step in the same item.
    /// </summary>
    [Fact]
    public async Task ItemsOfOneForEach_TheOneIssueStopped_EachHaveItFixed_OnTheirOwnBound()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              maxVisits: 1
              steps:
                - name: mend
                  type: orient
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: work
                    type: reason
                    prompt: work.md
                    outputs: work.json
                    onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var fixedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Step.EndsWith("mend", StringComparison.Ordinal) && step.State == FlowInstanceState.Done));
        _ = watched.Seen.ContinueWith(_ => fixedOnce.TrySetResult(), TaskScheduler.Default);
        var blocked = FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)]);
        var runner = new FakeRunner()
            .On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["a", "b"]}""")))
            .OnAsync("work", async run => run.Instance switch
            {
                "items[0].work" => blocked,

                // The second item met the issue before the first item's fix was done, and says so once that fix is.
                "items[1].work" => await fixedOnce.Task.ContinueWith(_ => blocked, TaskScheduler.Default),
                _ => FlowStepResult.Ended(passed: true, HarnessExit.Success),
            })
            .On("triage", run => Course("fix", run.Issue!.Title))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.DoesNotContain(coordinator.OpenQuestions, question => question.Kind == "recoveryBound");
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items[0].work+2"].State);
        // The second item's fix is a first visit of the recovery on its own bound, not a second one on the first item's.
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["recover+2"].State);
        Assert.Equal(1, coordinator.Instances["recover+2"].Visit);
    }

    /// <summary>
    /// A step that began once the fix was done met the issue after it, though the clock reads both in the same second:
    /// the engine's own order says which came first, and the report is triaged anew rather than held by the fix.
    /// </summary>
    [Fact]
    public async Task AStepThatBeganOnceTheFixWasDone_InTheSameSecond_MetTheIssueAfterIt()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: gate
                type: orient
              - name: later
                type: reason
                prompt: work.md
                outputs: work.json
                dependsOn: [gate]
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var fixedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Step == "recover" && step.State == FlowInstanceState.Done));
        _ = watched.Seen.ContinueWith(_ => fixedOnce.TrySetResult(), TaskScheduler.Default);
        var runner = new FakeRunner()
            .On("think", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .OnAsync("gate", async _ =>
            {
                await fixedOnce.Task;
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            })
            .On("later", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser")]))
            .On("triage", run => Course(run.Issue!.Blocks ? "fix" : "list", run.Issue.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Contains(("triage+2", "a hole in the parser"), runner.Started);
        Assert.Same(coordinator.Instances["later"], coordinator.Instances["triage+2"].RaisedBy);
    }

    /// <summary>
    /// A step run again after a failure - a person answering it retry - met the issue in the attempt that reported
    /// it: one begun after the fix was done met it after the fix, though its first attempt began before, and the issue
    /// is triaged anew.
    /// </summary>
    [Fact]
    public async Task AnAttemptBegunAfterTheFix_MetTheIssueAfterIt_ThoughTheStepsFirstAttemptBeganBefore()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: review
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var fixedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Step == "recover" && step.State == FlowInstanceState.Done));
        _ = watched.Seen.ContinueWith(_ => fixedOnce.TrySetResult(), TaskScheduler.Default);
        var runner = new FakeRunner()
            .On("think", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .OnAsync("review", async run =>
            {
                if (run.Attempt == 1)
                {
                    // The first attempt began before the fix and fails once it is done; the next begins after it.
                    await fixedOnce.Task;
                    return FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the agent stopped");
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser")]);
            })
            .On("triage", run => Course(run.Issue!.Blocks ? "fix" : "list", run.Issue.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);

        var first = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The review's first attempt failed once the fix was done, and a person answers retry: the next attempt begins.
        Assert.Equal(FlowRunStatus.Paused, first.Status);
        var asked = coordinator.OpenQuestions.Single(question => question.Kind == "failure" && question.Instance == "review");
        Assert.True(coordinator.TryAnswer(asked.Id, "retry", null, out _));
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(2, coordinator.Instances["review"].Attempt);
        Assert.Same(coordinator.Instances["review"], coordinator.Instances["triage+2"].RaisedBy);
    }

    /// <summary>
    /// A triage that failed triaged nothing and holds nothing: the issue it was raised for, reported again, raises a
    /// triage anew rather than being lost behind the failure.
    /// </summary>
    [Fact]
    public async Task AnIssueWhoseTriageFailed_IsRaisedAnewWhenReportedAgain()
    {
        var definition = Parse(TwoReporters.Replace("onAddress: recover", "onAddress: recover\n    onFailure: continue", StringComparison.Ordinal));
        var runner = new FakeRunner()
            .On("first", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", asked => asked == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed)
                : Course("unproven", "a broken test"));
        runner.Hold("second");
        var watched = new Watched(null, snapshot => snapshot.Steps.Any(step => step.Step == "triage" && step.State == FlowInstanceState.Failed));
        _ = watched.Seen.ContinueWith(
            _ => runner.Release("second", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")])),
            TaskScheduler.Default);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: watched);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["triage"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["triage+2"].State);
        Assert.Equal("a broken test", coordinator.Instances["triage+2"].Issue!.Title);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A flow of two reporting steps and the recovery, both naming the one triage.</summary>
    private const string TwoReporters = """
        name: flow
        recover:
          onSuccess: failed-step
          onFailure: pause
          steps:
            - name: mend
              type: orient
        steps:
          - name: first
            type: reason
            prompt: work.md
            outputs: work.json
            onPreExisting: triage
          - name: second
            type: reason
            prompt: work.md
            outputs: work.json
            onPreExisting: triage
          - name: triage
            type: reason
            prompt: triage.md
            outputs: pre-existing.json
            raised: preExisting
            onAddress: recover
        """;

    /// <summary>A triage answer that says no course the engine takes - none at all, or one it does not know - fails the triage step, and the issue vanishes nowhere unsaid.</summary>
    [Theory]
    [InlineData("""{"title": "a broken test"}""", "its answer says no course: it is one of fix, list, next, duplicate, unproven, join")]
    [InlineData("""{"course": 7, "title": "a broken test"}""", "its answer says no course: it is one of fix, list, next, duplicate, unproven, join")]
    [InlineData("""{"course": "later", "title": "a broken test"}""", "its answer's course 'later' is none of fix, list, next, duplicate, unproven, join")]
    public async Task ATriageAnswerWithNoCourseTheEngineTakes_FailsTheTriageStep(string answer, string error)
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
                onFailure: continue
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output(answer)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var triage = coordinator.Instances["triage"];
        Assert.Equal(FlowInstanceState.Failed, triage.State);
        Assert.Equal(HarnessExit.ConfigInvalid, triage.Exit);
        Assert.Equal(error, triage.Error);
    }

    /// <summary>
    /// A proof of two megabytes - a failing test's whole output - is kept whole in the run's list, as the triage gave it,
    /// and the issue is listed: a proof is never cut to a length, and a long one never fails the triage step.
    /// </summary>
    [Fact]
    public async Task ATwoMegabyteProof_IsKeptWhole()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
                onFailure: stop
            """);
        var quote = string.Join("\n", Enumerable.Range(1, 2 * 1024 * 1024 / 32).Select(line => $"FAIL test {line,8}: expected 1, was 2"));
        var answer = (JsonObject)Course("list", "a broken test").Output!.DeepClone();
        answer["proof"]!["quote"] = quote;
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: answer));
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();

        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);

            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["triage"].State);
        }

        var issues = store.ReadPreExistingIssues(layout);

        Assert.NotNull(issues);
        Assert.True(quote.Length >= 2 * 1024 * 1024, $"{quote.Length} characters");
        Assert.Equal(quote, Assert.Single(issues.Issues).Proof.Quote);
    }

    /// <summary>A triage answer whose issue does not read back fails the triage step: a record is never guessed at.</summary>
    [Fact]
    public async Task ATriageAnswerWhoseIssueDoesNotReadBack_FailsTheTriageStep()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
                onFailure: stop
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"course": "list", "title": "a broken test", "description": "what", "where": ["src/Parser.cs"], "why": "not related", "proof": {"kind": "read"}}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["triage"].State);
        Assert.Contains("does not read back", coordinator.Instances["triage"].Error, StringComparison.Ordinal);
    }

    /// <summary>The issues a run listed are its file's: a resume by another engine restores them, and its words are settled.</summary>
    [Fact]
    public async Task TheIssuesARunListed_AreRestoredAtAResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: later
                type: orient
                dependsOn: [think]
              - name: tell
                type: pause
                dependsOn: [later]
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var first = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("a stale document")]))
            .On("triage", run => Course("list", run.Issue!.Title));
        first.Hold("later");
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();

        FlowResumeState taken;
        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var watched = new Watched(records, BothTriaged);
            var coordinator = new FlowCoordinator(definition, first, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            // Both issues are triaged - the pause is read by the run's own loop after both answers - and then it pauses.
            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt", all: true);
            var end = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, end.Status);

            var issues = store.ReadPreExistingIssues(layout);

            Assert.NotNull(issues);
            Assert.Equal(["pe-1", "pe-2"], issues.Issues.Select(issue => issue.Id));
            Assert.Equal(["a broken test", "a stale document"], issues.Issues.Select(issue => issue.Title));

            taken = new FlowResume(store, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));
        }

        using (var again = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = new FlowCoordinator(definition, new FakeRunner().On("later", FlowStepResult.Ended(passed: true, HarnessExit.Success)), new RunnerFacts(), clock, records: again, resume: taken);
            var done = await resumed.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, done.Status);

            var asked = resumed.OpenQuestions.Single(question => question.Kind == "pause");
            Assert.Equal("what was there before: pe-1: listed - a broken test (src/Parser.cs); pe-2: listed - a stale document (src/Parser.cs)", asked.Problem);
        }
    }

    /// <summary>
    /// A join is read again from the records at a resume: the issue joined to one whose visit a pause stopped goes with
    /// that visit still, takes no visit of its own, and is fixed with it once the visit ends in the next session.
    /// </summary>
    [Fact]
    public async Task AJoin_IsReadAgainAtAResume_AndSettledWithTheVisitItWentWith()
    {
        using var temp = new TempDirectory();
        var definition = Parse(OneCause);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole"), Issue("the second hole")]))
            .OnAsync("triage", async run =>
            {
                if (run.Issue!.Title == "the second hole")
                {
                    return Joins(run.Issue.Title, "triage");
                }

                await joined.Task;
                return Course("fix", run.Issue.Title);
            });
        first.Hold("mend");
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();
        var cancel = TestContext.Current.CancellationToken;

        FlowResumeState taken;
        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var watched = new Watched(records, snapshot => snapshot.Steps.Any(step => step.Instance == "triage+2" && step.State == FlowInstanceState.Done));
            _ = watched.Seen.ContinueWith(_ => joined.TrySetResult(), TaskScheduler.Default);
            var coordinator = new FlowCoordinator(definition, first, new RunnerFacts(), clock, records: watched, workflowFile: (_, named) => OneCausePrompt(named));
            var session = coordinator.RunAsync(cancel);

            await watched.Until(snapshot => snapshot.Steps.Any(step => step.Instance == "recover.mend" && step.State == FlowInstanceState.Running)).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            coordinator.RequestPause("interrupt", all: true);
            Assert.Equal(FlowRunStatus.Paused, (await session.WaitAsync(TimeSpan.FromSeconds(30), cancel)).Status);

            taken = new FlowResume(store, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));
        }

        using (var again = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var watched = new Watched(again, _ => false);
            var resumed = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), clock, records: watched, resume: taken, workflowFile: (_, named) => OneCausePrompt(named));
            var done = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            // The always step asked as the resumed visit began; the run's counts, written as the visit ended, say both fixed.
            Assert.Equal(FlowRunStatus.Paused, done.Status);
            Assert.Equal(FlowInstanceState.Done, resumed.Instances["recover"].State);
            Assert.False(resumed.Instances.ContainsKey("recover+2"));
            Assert.Equal(new WorkflowPreExisting(0, 2, 0, 0), watched.Last!.PreExisting);
        }
    }

    /// <summary>
    /// The run's words of the classes its items closed are gathered by their one owner from the steps that judge and
    /// count them, known by their type: each item's record as its newest class-closure check that ended done judged it
    /// - a check run again after a rework is the one that stands - whole, and each class's claim alone; the hotspots
    /// that check named; the classes the item's newest routing says its reviews reopened, with how many rounds; and each
    /// item's counts, read from that record and those routings. A plan audit's own closure member is no item's record,
    /// and an item no check judged - one whose checks were skipped - has none; where the run holds none of these, each
    /// says none.
    /// </summary>
    [Fact]
    public async Task TheRunsWords_GatherEachItemsNewestJudgedRecord_ByTheStepsThatJudgeAndCountIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: audit
                type: reason
                prompt: work.md
                outputs: work.json
              - name: items
                forEach: 'audit.items'
                dependsOn: [audit]
                steps:
                  - name: make
                    type: create-agent
                    agent: worker.md
                    when: "item.name == 'a'"
                  - name: closing
                    type: class-closure
                    in: make
                    when: "item.name == 'a'"
                    dependsOn: [make]
                    classes: 'item.classes'
                    record: 'item.record'
                  - name: route
                    type: route-findings
                    in: make
                    when: "item.name == 'a'"
                    dependsOn: [closing]
                    findings: 'item.findings'
                    classes: 'item.classes'
                    record: 'item.record'
                  - name: reclosing
                    type: class-closure
                    in: make
                    when: "item.name == 'a'"
                    dependsOn: [route]
                    classes: 'item.classes'
                    record: 'item.record'
              - name: tell
                type: pause
                always: true
                question: |-
                  records: {run.closure}
                  claims: {run.claims}
                  reopened: {run.reopened}
                  hotspots: {run.hotspots}
                  counts: {run.counts}
                options: [ok]
            """);
        FlowStepResult Answer(JsonObject output) => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: output);
        JsonObject Class(string @class, string claim) => new() { ["class"] = @class, ["claim"] = claim };
        JsonObject Closed(string @class, string claim)
        {
            var closed = Class(@class, claim);
            closed["sites"] = new JsonArray(new JsonObject { ["path"] = "src/Old.cs", ["after"] = "reads-home" }, new JsonObject { ["path"] = "docs/old.md", ["after"] = "deleted" });
            closed["universe"] = new JsonObject { ["axes"] = new JsonArray(), ["cellsPinned"] = 3, ["cellsArmed"] = 0 };
            closed["neighbours"] = new JsonArray(new JsonObject { ["cell"] = "md", ["result"] = "unmeasured", ["reason"] = "no reader" });
            return closed;
        }
        JsonObject Item(string name) => new() { ["name"] = name, ["classes"] = new JsonArray(), ["record"] = new JsonArray(), ["findings"] = new JsonArray() };
        JsonObject Checked(JsonArray record, JsonArray hotspots) => new()
        {
            ["passed"] = true,
            ["problems"] = new JsonArray(),
            ["hotspots"] = hotspots,
            ["touches"] = new JsonArray(),
            ["record"] = record,
        };
        var runner = new FakeRunner()
            .On("audit", (FlowStepRun _) => Answer(new JsonObject
            {
                ["items"] = new JsonArray(Item("a"), Item("b")),
                ["closure"] = new JsonArray(new JsonObject { ["class"] = "c-plan", ["missingSites"] = new JsonArray("src/Plan.cs"), ["missingCells"] = new JsonArray() }),
            }))
            .On("closing", (FlowStepRun _) => Answer(Checked(new JsonArray(Class("c-rate", "named a case: one format")), new JsonArray())))
            .On("route", (FlowStepRun _) => Answer(new JsonObject
            {
                ["rework"] = new JsonArray(),
                ["homeAudit"] = new JsonArray(),
                ["reopened"] = new JsonArray(new JsonObject { ["class"] = "c-rate", ["rounds"] = 2 }),
            }))
            .On("reclosing", (FlowStepRun _) => Answer(Checked(
                new JsonArray(Closed("c-rate", "the rate holds over every format"), Class("c-frame", "the frame's size is decided once")),
                new JsonArray(new JsonObject { ["class"] = "c-rate", ["path"] = "src/Rates/Home.cs", ["touches"] = 6 }))));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(
            "records: [{\"class\":\"c-rate\",\"claim\":\"the rate holds over every format\",\"sites\":[{\"path\":\"src/Old.cs\",\"after\":\"reads-home\"},{\"path\":\"docs/old.md\",\"after\":\"deleted\"}],"
                + "\"universe\":{\"axes\":[],\"cellsPinned\":3,\"cellsArmed\":0},\"neighbours\":[{\"cell\":\"md\",\"result\":\"unmeasured\",\"reason\":\"no reader\"}]},"
                + "{\"class\":\"c-frame\",\"claim\":\"the frame's size is decided once\"}]\n"
                + "claims: 'c-rate': the rate holds over every format; 'c-frame': the frame's size is decided once\n"
                + "reopened: 'c-rate' reopened in 2 round(s)\n"
                + "hotspots: 'c-rate' home 'src/Rates/Home.cs' touched 6 times\n"
                + "counts: 'items[0]': 1 review round(s), 0 home audit(s), classes 'c-rate' (1 site(s) consolidated, 1 deleted, 3 cell(s) pinned, "
                + "0 neighbour(s) measured, 1 unmeasured, sent back 2 time(s)), 'c-frame' (0 site(s) consolidated, 0 deleted, 0 cell(s) pinned, "
                + "0 neighbour(s) measured, 0 unmeasured, sent back 0 time(s))",
            coordinator.OpenQuestions.Single(question => question.Kind == "pause").Problem);

        // A run whose items no check judged says none of each.
        var none = new FlowCoordinator(
            definition,
            new FakeRunner().On("audit", (FlowStepRun _) => Answer(new JsonObject { ["items"] = new JsonArray(Item("b")), ["closure"] = new JsonArray() })),
            new RunnerFacts(),
            new JumpingClock());

        _ = await none.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("records: none\nclaims: none\nreopened: none\nhotspots: none\ncounts: none", none.OpenQuestions.Single(question => question.Kind == "pause").Problem);
    }

    /// <summary>The issues a run owes its next round are their file's too: a resume by another engine restores them, and the run's words say what became of each.</summary>
    [Fact]
    public async Task TheIssuesARunOwesItsNextRound_AreRestoredAtAResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: later
                type: orient
                dependsOn: [think]
              - name: tell
                type: pause
                dependsOn: [later]
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var answer = (JsonObject)Course("next", "a broken test").Output!.DeepClone();
        answer["size"] = new JsonObject { ["files"] = new JsonArray("src/Parser.cs"), ["build"] = "", ["measureFirst"] = new JsonArray() };
        var first = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: answer));
        first.Hold("later");
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();

        FlowResumeState taken;
        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var watched = new Watched(records, snapshot => snapshot.Steps.Any(step => step.Step == "triage" && step.State is FlowInstanceState.Done or FlowInstanceState.Failed));
            var coordinator = new FlowCoordinator(definition, first, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt", all: true);
            var end = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, end.Status);

            var owed = store.ReadNextRound(layout);

            Assert.NotNull(owed);
            Assert.Equal(("nx-1", "a broken test", "src/Parser.cs"), (owed.Issues[0].Id, owed.Issues[0].Title, owed.Issues[0].Files[0]));

            taken = new FlowResume(store, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));
        }

        using (var again = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = new FlowCoordinator(definition, new FakeRunner().On("later", FlowStepResult.Ended(passed: true, HarnessExit.Success)), new RunnerFacts(), clock, records: again, resume: taken);
            var done = await resumed.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, done.Status);

            var asked = resumed.OpenQuestions.Single(question => question.Kind == "pause");
            Assert.True(
                asked.Problem == "what was there before: nx-1: next round - a broken test (src/Parser.cs)",
                asked.Problem);
        }
    }

    /// <summary>
    /// An issue the run owes its next round is given to a reader as the schema that types <c>run.nextRound</c> says it
    /// is - each member that schema names, in order, every one present and of its type - for both are made from the one
    /// list of the value's members; what settled it is the run's file's, never the value's.
    /// </summary>
    [Fact]
    public void AnIssueOwed_IsGivenAsTheSchemaTypingRunNextRoundSays()
    {
        var issue = new FlowNextIssue("nx-1", "a broken test", "what it is", ["src/Parser.cs"], ["src/Parser.cs", "src/Lexer.cs"], "the parser", ["its cells"], "triage", DateTimeOffset.UnixEpoch) { SettledBy = "rounds#2.items[0]" };
        var schema = FlowNextIssue.ValueSchema();

        Assert.Empty(DssHarness.Core.Schemas.JsonSchemaSubset.SubsetProblems(schema));
        Assert.Empty(DssHarness.Core.Schemas.JsonSchemaSubset.Problems(schema, issue.Value()));
        Assert.Equal(
            """{"id":"nx-1","title":"a broken test","description":"what it is","where":["src/Parser.cs"],"files":["src/Parser.cs","src/Lexer.cs"],"build":"the parser","measureFirst":["its cells"]}""",
            issue.Value().ToJsonString());
        Assert.Equal(
            ["id", "title", "description", "where", "files", "build", "measureFirst"],
            [.. ((JsonArray)schema["required"]!).Select(member => member!.GetValue<string>())]);
        Assert.Equal(
            ["id", "title", "description", "where", "files", "build", "measureFirst"],
            [.. ((JsonObject)schema["properties"]!).Select(member => member.Key)]);
    }

    /// <summary>
    /// An issue the run owes its next round is owed still where the engine stopped after the triage's end was written
    /// and before the run's file of the issues was: the resume reads it from the triage's own recorded answer, the run's
    /// words say it, and the file holds it again.
    /// </summary>
    [Fact]
    public async Task AnIssueOwedTheNextRound_IsOwedStill_WhereTheEngineStoppedBeforeItsFileWasWritten()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: later
                type: orient
                dependsOn: [think]
              - name: tell
                type: pause
                dependsOn: [later]
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var answer = (JsonObject)Course("next", "a broken test").Output!.DeepClone();
        answer["size"] = new JsonObject { ["files"] = new JsonArray("src/Parser.cs"), ["build"] = "", ["measureFirst"] = new JsonArray() };
        var first = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: answer));
        first.Hold("later");
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();

        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var watched = new Watched(records, snapshot => snapshot.Steps.Any(step => step.Step == "triage" && step.State is FlowInstanceState.Done or FlowInstanceState.Failed));
            var coordinator = new FlowCoordinator(definition, first, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt", all: true);
            Assert.Equal(FlowRunStatus.Paused, (await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status);
        }

        // The engine stopped where the triage's end was written and the run's file of the issues was not: the file holds
        // what the session's start wrote, no issue.
        store.WriteNextRound(layout, new FlowNextRoundFile(1, "r1", clock.GetUtcNow(), []));
        var taken = new FlowResume(store, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));

        using (var again = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = new FlowCoordinator(definition, new FakeRunner().On("later", FlowStepResult.Ended(passed: true, HarnessExit.Success)), new RunnerFacts(), clock, records: again, resume: taken);
            Assert.Equal(FlowRunStatus.Paused, (await resumed.RunAsync(TestContext.Current.CancellationToken)).Status);

            Assert.Equal("what was there before: nx-1: next round - a broken test (src/Parser.cs)", resumed.OpenQuestions.Single(question => question.Kind == "pause").Problem);
        }

        var owed = Assert.Single(store.ReadNextRound(layout)!.Issues);
        Assert.Equal(("nx-1", "a broken test", "triage", (string?)null), (owed.Id, owed.Title, owed.FoundBy, owed.SettledBy));
    }

    /// <summary>
    /// An issue the run owes its next round is held to by the check that opens a round alone - a validate-plan beside
    /// the forEach that expands from it, in an iteration of a repeat - and never by another check, whose items are no
    /// round's; it is settled by the work item that names it in settles once that item ended done with a commit of its
    /// own made, whether the run takes plan updates or not: an item that ended done with nothing committed - as one a
    /// person skipped does - settles nothing, and the next round holds the issue again. The run's file says which item
    /// settled it and the run's words say it was fixed; and where the engine stopped after the item's end was written
    /// and before the file took the settling, the resume settles it again from the item's own records.
    /// </summary>
    [Fact]
    public async Task AnIssueOwedTheNextRound_IsHeldByTheRoundsCheckAlone_AndSettledByAnItemWhoseWorkLanded()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: side-check
                type: validate-plan
                items: 'think.items'
                dependsOn: [think]
              - name: rounds
                dependsOn: [think]
                repeat:
                  until: 'empty(run.nextRound)'
                  maxIterations: 3
                steps:
                  - name: plan
                    type: reason
                    prompt: plan.md
                    outputs: plan.json
                  - name: item-check
                    type: validate-plan
                    items: 'plan.items'
                    dependsOn: [plan]
                  - name: items
                    forEach: 'item-check.items'
                    dependsOn: [item-check]
                    steps:
                      - name: checkpoint
                        type: commit
                        message: checkpoint
              - name: later
                type: orient
                dependsOn: [rounds, side-check]
              - name: tell
                type: pause
                dependsOn: [later]
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var answer = (JsonObject)Course("next", "a broken test").Output!.DeepClone();
        answer["size"] = new JsonObject { ["files"] = new JsonArray("src/Parser.cs"), ["build"] = "", ["measureFirst"] = new JsonArray() };
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonNode Settling() => new JsonArray(new JsonObject { ["name"] = "fix-the-parser", ["settles"] = "nx-1" });
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();
        var given = new Dictionary<string, string?>(StringComparer.Ordinal);

        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var watched = new Watched(records, snapshot => snapshot.Steps.Any(step => step.Step == "triage" && step.State is FlowInstanceState.Done or FlowInstanceState.Failed));

            // Every check, the rounds' and the other, starts once the issue is owed; each says what it was given.
            FlowStepResult Checked(FlowStepRun run, JsonNode items)
            {
                lock (given)
                {
                    given[run.Instance] = run.Read is { } read && read.TryGetValue("next", out var next) ? next.ToNode()?.ToJsonString() : null;
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["ok"] = true, ["problems"] = new JsonArray(), ["items"] = items });
            }

            var runner = new FakeRunner()
                .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["items"] = new JsonArray() }, preExisting: [Issue("a broken test")]))
                .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: answer))
                .OnAsync("side-check", async run =>
                {
                    await watched.Seen;
                    return Checked(run, new JsonArray());
                })
                .OnAsync("plan", async run =>
                {
                    await watched.Seen;
                    return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["items"] = Settling() });
                })
                .On("item-check", run => Checked(run, Settling()))

                // The first round's item lands nothing - its commit took no path - and the second's lands its fix.
                .On("checkpoint", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
                {
                    ["committed"] = run.Instance.StartsWith("rounds#2.", StringComparison.Ordinal),
                    ["sha"] = run.Instance.StartsWith("rounds#2.", StringComparison.Ordinal) ? "0123456789abcdef0123456789abcdef01234567" : null,
                    ["paths"] = new JsonArray(),
                    ["ignored"] = new JsonArray(),
                }))

                // The step after the rounds holds the run until the test pauses it.
                .OnAsync("later", _ =>
                {
                    later.TrySetResult();
                    return new TaskCompletionSource<FlowStepResult>().Task;
                });
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            await later.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt", all: true);
            Assert.Equal(FlowRunStatus.Paused, (await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status);

            var owed = """[{"id":"nx-1","title":"a broken test","description":"what it is","where":["src/Parser.cs"],"files":["src/Parser.cs"],"build":"","measureFirst":[]}]""";

            Assert.Equal(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["side-check"] = null,
                    ["rounds#1.item-check"] = owed,
                    ["rounds#2.item-check"] = owed,
                },
                given);
            Assert.DoesNotContain(runner.Started, started => started.Instance.StartsWith("rounds#3.", StringComparison.Ordinal));
        }

        Assert.Equal("rounds#2.items[0]", Assert.Single(store.ReadNextRound(layout)!.Issues).SettledBy);

        // The engine stopped where the item's end was written and the run's file of the issues was not.
        var file = store.ReadNextRound(layout)!;
        store.WriteNextRound(layout, file with { Issues = [.. file.Issues.Select(issue => issue with { SettledBy = null })] });
        var taken = new FlowResume(store, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));

        using (var again = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), clock, records: again, resume: taken);
            Assert.Equal(FlowRunStatus.Paused, (await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status);

            Assert.Equal("what was there before: nx-1: fixed in a later round - a broken test (src/Parser.cs)", resumed.OpenQuestions.Single(question => question.Kind == "pause").Problem);
        }

        Assert.Equal("rounds#2.items[0]", Assert.Single(store.ReadNextRound(layout)!.Issues).SettledBy);
    }
}
