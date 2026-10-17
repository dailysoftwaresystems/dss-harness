using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// How a workflow's definition file is read: strict as an action file is, every key checked against the table that
/// owns it, every problem found reported together with the line it was read at, and nothing set that nothing reads.
/// </summary>
public sealed class WorkflowFileParserTests
{
    /// <summary>Parses <paramref name="text"/>, answering its refusal rather than throwing it.</summary>
    private static WorkflowDefinition? Parse(string text)
    {
        try
        {
            return WorkflowFileParser.Parse("workflows/implementation/default.yml", text);
        }
        catch (HarnessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Problems(string text)
    {
        var refusal = Assert.Throws<HarnessException>(() => WorkflowFileParser.Parse("workflows/implementation/default.yml", text));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);

        return refusal.Message.Split(Environment.NewLine).Skip(1).Select(line => line.TrimStart(' ', '-', ' ')).ToList();
    }

    /// <summary>Asserts one problem says <paramref name="expected"/> somewhere in it, as a refusal is read for the part that concerns it.</summary>
    private static void Has(string expected, IReadOnlyList<string> problems)
        => Assert.Contains(problems, problem => problem.Contains(expected, StringComparison.Ordinal));

    /// <summary>The least a file can be: a name and one step, which the whole of the model reads back.</summary>
    [Fact]
    public void TheLeastAFileCanBe_IsANameAndOneStep()
    {
        var parsed = Parse(
            """
            name: default
            steps:
              - name: orient
                type: orient
            """);

        Assert.NotNull(parsed);
        Assert.Equal("default", parsed!.Name);
        Assert.Equal("orient", Assert.Single(parsed.Steps).Name);
        Assert.Equal("orient", parsed.Steps[0].Type);
    }

    [Fact]
    public void AFileThatNamesNoNameOrNoSteps_IsRefusedNamingBoth()
    {
        var problems = Problems("description: nothing\n");

        Has("the file names no 'name'", problems);
        Has("the file declares no 'steps'", problems);
    }

    [Fact]
    public void AnUnknownKey_IsRefusedWithTheLineItWasReadAt_AndEveryKnownKey()
    {
        var problems = Problems(
            """
            name: default
            maximReasoningAgents: 4
            steps:
              - name: orient
                type: orient
            """);

        Has("line 2, column 1: 'maximReasoningAgents' is not a key of the file's top level. Known: 'name', 'description', 'maxReasoningAgents', 'maxParallelProcesses', 'maxOpenAgents', 'maxRetries', 'backoffSeconds', 'maxInstances', 'pushCheckpoints', 'handoffSeconds', 'budget', 'recommendations', 'advisor', 'reporter', 'amend', 'answers', 'inputs', 'params', 'outputs', 'steps', 'recover'.", problems);
    }

    /// <summary>
    /// A key written twice is refused by YAML itself, as a file that does not read: the second would quietly take the
    /// first's place, and which value ran would depend on where each sat in the file.
    /// </summary>
    [Fact]
    public void AKeyWrittenTwice_IsRefusedRatherThanSilentlyTakingTheLast()
    {
        var problems = Problems(
            """
            name: default
            name: other
            steps:
              - name: orient
                type: orient
            """);

        Has("line 2, column 1: the file is not valid YAML", problems);
        Has("name", problems);
    }

    [Fact]
    public void AStepOfNoTypeAndNoSteps_AndOneOfBoth_IsRefusedNamingEach()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: loose
              - name: both
                type: orient
                steps:
                  - name: orient
                    type: orient
            """);

        Has("line 3, column 5: loose names no type and holds no steps", problems);
        Has("line 4, column 5: both names the type 'orient' and holds steps", problems);
    }

    /// <summary>
    /// A key that belongs to a step this one is not is refused naming the type that takes it, and a key no type takes
    /// is refused where the keys were collected: neither reaches a reader.
    /// </summary>
    [Fact]
    public void AKeyOfAnotherStepType_IsRefusedNamingTheTypeThatTakesIt()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: gate
                type: test
                runner: corpus
            """);

        Has("line 5, column 13: 'runner' is not a key of a test step; it is one of a run step.", problems);
    }

    [Fact]
    public void AnUnknownStepType_IsRefusedWithEveryKnownOne()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: gate
                type: tests
            """);

        Has("'tests' is not a step type. Known: 'orient', 'reason', 'build', 'test', 'run', 'check-mutations', 'create-agent', 'fold', 'delete-agent', 'refresh', 'rebase', 'validate-plan', 'anchor-balance', 'anchor-citations', 'ci-read', 'ci-wait', 'commit', 'push', 'pull-request', 'pause'.", problems);
    }

    /// <summary>
    /// The keys a type requires are refused by name, one sentence for each the file leaves out, and the values a key
    /// takes from a closed list are refused naming the list.
    /// </summary>
    [Fact]
    public void AReasonStepWithoutItsRequiredKeys_AndAClosedListWronglySpelled_AreEachRefused()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: plan
                type: reason
              - name: gate
                type: test
                failOn: sometimes
            """);

        Has("line 3, column 5: plan names no 'prompt'; a reason step is refused without it.", problems);
        Has("line 3, column 5: plan names no 'outputs'; a reason step is refused without it.", problems);
        Has("'gate.failOn' is 'sometimes', which is none of 'red', 'never'.", problems);
    }

    [Fact]
    public void AValueOfTheWrongShape_IsRefusedNamingTheKeyItWasReadFor()
    {
        var problems = Problems(
            """
            name: default
            maxReasoningAgents: four
            steps:
              - name: orient
                type: orient
                dependsOn: orient
            """);

        Has("'maxReasoningAgents' is 'four', which is not a whole number.", problems);
        Has("line 6, column 16: 'orient.dependsOn' is not a list.", problems);
    }

    /// <summary>
    /// A key that takes a list written out or an expression reads a scalar as the expression and a sequence as the
    /// list, and nothing else is read of it here.
    /// </summary>
    [Fact]
    public void AListOrExpressionKey_ReadsAScalarAsAnExpressionAndASequenceAsTheList()
    {
        var parsed = Parse(
            """
            name: default
            steps:
              - name: make
                type: create-agent
                agent: implementer
              - name: implement
                type: reason
                access: write
                in: make
                prompt: prompts/implement.md
                outputs: schemas/implement.schema.json
                writes: item.ownedPaths
              - name: sweep
                type: fold
                of: make
                within:
                  - src/a.c
                  - src/b.c
            """);

        Assert.NotNull(parsed);
        Assert.Equal("item.ownedPaths", parsed!.Steps[1].Writes?.Expression);
        Assert.Empty(parsed.Steps[1].Writes!.Written);
        Assert.Equal(["src/a.c", "src/b.c"], parsed.Steps[2].Within?.Written);
        Assert.Null(parsed.Steps[2].Within?.Expression);
    }

    [Fact]
    public void AnAnswersMap_ReadsTheAnswerAloneAndTheLongSpelling()
    {
        var parsed = Parse(
            """
            name: default
            answers:
              loopBound: accept
              turnLimit:
                answer: retry
                times: 2
            steps:
              - name: orient
                type: orient
            """);

        Assert.NotNull(parsed);
        Assert.Equal(new FlowAnswer("accept", null, null), parsed!.Answers["loopBound"]);
        Assert.Equal(new FlowAnswer("retry", 2, null), parsed.Answers["turnLimit"]);
    }

    /// <summary>Steps nest no deeper than the bound the help states; the file that tries is refused at the level past it.</summary>
    [Fact]
    public void StepsNestingPastTheBound_AreRefusedWhereTheBoundIsPassed()
    {
        var text = new System.Text.StringBuilder("name: default\nsteps:\n");
        Step(1, WorkflowFileParser.MaxDepth + 1);

        void Step(int depth, int deepest)
        {
            var indent = new string(' ', depth * 2);
            text.Append($"{indent}- name: step-{depth}\n");

            if (depth < deepest)
            {
                text.Append($"{indent}  steps:\n");
                Step(depth + 1, deepest);
            }
            else
            {
                text.Append($"{indent}  type: orient\n");
            }
        }
        var problems = Problems(text.ToString());

        Has($"holds steps {WorkflowFileParser.MaxDepth + 1} levels deep; steps nest at most {WorkflowFileParser.MaxDepth}.", problems);
    }

    /// <summary>
    /// A typed step names no group key - forEach, repeat, gather - and a fold names no others, which only a refresh and
    /// a rebase take: each is refused with what takes it, where it would otherwise be read by nothing and obeyed by
    /// nothing.
    /// </summary>
    [Fact]
    public void AKeyOfAGroupOnATypedStep_IsRefusedWithWhatTakesIt()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
                forEach: 'input.items'
                repeat:
                  until: 'orient.ok'
                  maxIterations: 2
                gather: [findings]
              - name: fold
                type: fold
                of: orient
                others: orient
            """);

        Assert.Equal(
            [
                "line 5, column 14: 'forEach' is not a key of a orient step; it is one of a group, which names no type.",
                "line 7, column 7: 'repeat' is not a key of a orient step; it is one of a group, which names no type.",
                "line 9, column 13: 'gather' is not a key of a orient step; it is one of a group, which names no type.",
                "line 13, column 13: 'others' is not a key of a fold step; it is one of a refresh step.",
            ],
            problems);
    }

    /// <summary>
    /// An alias repeats a part of the file by reference, so a few hundred bytes could stand for millions of steps; and
    /// YAML nested past any definition's depth would be recursed through to be read at all. Both are refused before the
    /// file is read into nodes, with the line where each is met.
    /// </summary>
    [Fact]
    public void AnAliasOrNestingNoDefinitionHolds_IsRefusedBeforeTheFileIsRead()
    {
        var aliased = Problems(
            """
            name: default
            steps:
              - &step
                name: orient
                type: orient
              - *step
            """);

        Assert.Equal(["line 6, column 5: the alias '*step' repeats a part of the file by reference; a definition writes each of its steps out, so that what it says is what it reads."], aliased);

        var nested = Problems($"name: default\ndescription: {new string('[', 20_000)}\nsteps: []\n");

        Assert.Equal([$"line 2, column {WorkflowFileParser.MaxYamlDepth + 13}: the file nests more than {WorkflowFileParser.MaxYamlDepth} levels deep; no definition does, and steps themselves nest at most {WorkflowFileParser.MaxDepth}."], nested);
    }

    /// <summary>
    /// A budget is a number a run can count down - never NaN or infinity; a loop past its rounds may be bounded by
    /// nothing but the budget, read as false; the advisor and the reporter each name the agent they run, which is
    /// required; and the recovery holds steps and says where the run goes once they are done, back to the step that failed.
    /// </summary>
    [Fact]
    public void ABudgetIsANumber_AnUnboundedLoopIsFalse_AndTheAgentsAndTheRecoveryAreWhole()
    {
        var problems = Problems(
            """
            name: default
            budget:
              usd: NaN
              minutes: .inf
            advisor:
              prompt: prompts/recommend.md
            reporter:
              statusPrompt: prompts/status.md
              askPrompt: prompts/ask.md
            recover:
              maxVisits: 2
              onSuccess: elsewhere
              onFailure: retry
            steps:
              - name: orient
                type: orient
            """);

        Assert.Equal(
            [
                "line 3, column 8: 'budget.usd' is 'NaN', which is not a number.",
                "line 4, column 12: 'budget.minutes' is '.inf', which is not a number.",
                "'advisor.agent' is named nowhere; it is required.",
                "'reporter.agent' is named nowhere; it is required.",
                "line 12, column 14: 'recover.onSuccess' is 'elsewhere', which is none of 'failed-step'; a recovery sends the run back to the step that gave it its failure, to run again once it was put right.",
                "line 13, column 14: 'recover.onFailure' is 'retry', which is none of 'stop', 'pause', 'continue', 'recover'.",
                "line 11, column 3: 'recover' holds no 'steps'; a recovery that runs nothing would put nothing right, and the step that failed would run again unchanged.",
            ],
            problems);

        var parsed = Parse(
            """
            name: default
            steps:
              - name: loop
                repeat:
                  until: 'review.clean'
                  rounds: 3
                  blocking: 'review.blocking'
                  blockersLimitRounds: false
                steps:
                  - name: review
                    type: orient
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: orient
            """);

        Assert.NotNull(parsed);
        Assert.Equal(new FlowRepeat("review.clean", null, 3, "review.blocking", null, null, BlockersUnbounded: true), parsed.Steps[0].Repeat);
        Assert.Equal("diagnose", Assert.Single(parsed.Recover!.Steps).Name);
        Assert.Null(parsed.Recover.MaxVisits);
        Assert.Null(parsed.BackoffSeconds);

        // A reasoning step's retries wait: the workflow's backoff, and a step's own beside it.
        var waiting = Parse(
            """
            name: default
            backoffSeconds: 45
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              retries: 2
              backoffSeconds: 15
            steps:
              - name: think
                type: reason
                agent: thinker
                prompt: prompts/think.md
                outputs: schemas/think.json
                retries: 4
                backoffSeconds: 10
            """);

        Assert.NotNull(waiting);
        Assert.Equal(45, waiting.BackoffSeconds);
        Assert.Equal(4, waiting.Steps[0].Retries);
        Assert.Equal(10, waiting.Steps[0].BackoffSeconds);
        Assert.Equal(2, waiting.Advisor!.Retries);
        Assert.Equal(15, waiting.Advisor.BackoffSeconds);
        Assert.Equal(FlowRecover.FailedStep, parsed.Recover.OnSuccess);
        Assert.Equal(FlowRecover.PauseOnFailure, parsed.Recover.OnFailure);
    }
}
