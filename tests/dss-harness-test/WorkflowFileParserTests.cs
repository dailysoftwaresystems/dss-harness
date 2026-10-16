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
            return WorkflowFileParser.Parse("workflows/default/default.yml", text);
        }
        catch (HarnessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Problems(string text)
    {
        var refusal = Assert.Throws<HarnessException>(() => WorkflowFileParser.Parse("workflows/default/default.yml", text));

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

        Has("line 2, column 1: 'maximReasoningAgents' is not a key of the file's top level. Known: 'name', 'description', 'maxReasoningAgents', 'maxParallelProcesses', 'maxOpenAgents', 'maxRetries', 'maxInstances', 'pushCheckpoints', 'handoffSeconds', 'budget', 'recommendations', 'advisor', 'reporter', 'amend', 'answers', 'inputs', 'params', 'outputs', 'steps'.", problems);
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

        Has($"holds steps {WorkflowFileParser.MaxDepth} levels deep; steps nest at most {WorkflowFileParser.MaxDepth}.", problems);
    }
}
