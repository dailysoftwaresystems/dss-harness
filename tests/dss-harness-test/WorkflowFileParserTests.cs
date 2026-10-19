using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// How a workflow's definition file is read: strict as an action file is, every key checked against the table that
/// owns it, every problem found reported together with the line it was read at, and nothing set that nothing reads.
/// </summary>
public sealed class WorkflowFileParserTests
{
    /// <summary>Where every file of these tests is said to have been read from.</summary>
    private const string FilePath = "workflows/implementation/implementation.yml";

    /// <summary>Parses <paramref name="text"/>, answering its refusal rather than throwing it.</summary>
    private static WorkflowDefinition? Parse(string text)
    {
        try
        {
            return WorkflowFileParser.Parse(FilePath, text);
        }
        catch (HarnessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every problem the refusal of <paramref name="text"/> lists, one to a line under its header, as the refusal's own
    /// layout lays them out: the header and the layout are held once, by the test that pins them.
    /// </summary>
    private static IReadOnlyList<string> Problems(string text)
    {
        var refusal = Assert.Throws<HarnessException>(() => WorkflowFileParser.Parse(FilePath, text));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);

        return refusal.Message.Split(Environment.NewLine).Skip(1).Select(line => line.TrimStart(' ', '-', ' ')).ToList();
    }

    /// <summary>
    /// YAML refuses a key written twice only where both are written alike; one tagged apart (!!str) comes through, and is
    /// refused here by name - a step's key, a kind an answers map names, and a name a map of values gives - never read in
    /// place of nothing, and never the tool's own failure.
    /// </summary>
    [Fact]
    public void AKeyWrittenTwice_TaggedApart_IsRefusedByName()
    {
        Assert.Equal(
            [
                "line 3, column 3: answers names the kind 'loopBound' more than once",
                "line 9, column 5: 'name' is written twice in a step; a key is one value",
                "line 15, column 7: run.answers names the kind 'loopBound' more than once",
                "line 21, column 7: 'run.inputs' names 'count' more than once",
            ],
            Problems(
                """
                name: x
                answers:
                  loopBound:
                    answer: fail
                  !!str loopBound:
                    answer: accept
                steps:
                  - name: build
                    !!str name: other
                    type: build
                  - name: run
                    type: run
                    runner: r
                    answers:
                      loopBound:
                        answer: fail
                      !!str loopBound:
                        answer: accept
                    inputs:
                      count: '1'
                      !!str count: '2'
                """));
    }

    /// <summary>
    /// A step is raised by a pre-existing issue and nothing else, and sends one to fix to the recovery and nowhere else:
    /// any other value of raised or onAddress is refused, naming the one it takes.
    /// </summary>
    [Fact]
    public void RaisedAndOnAddress_TakeTheirOneValue()
    {
        Assert.Equal(
            [
                "'triage.raised' is 'failure', which is none of 'preExisting'.",
                "'triage.onAddress' is 'pause', which is none of 'recover'.",
            ],
            Problems(
                """
                name: x
                steps:
                  - name: triage
                    type: reason
                    agent: pre-existing-triage
                    prompt: prompts/pre-existing.md
                    outputs: schemas/pre-existing.json
                    raised: failure
                    onAddress: pause
                """));
    }

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

    /// <summary>
    /// A refusal names the file and counts its problems, then lists each on a line of its own, so that a person reads
    /// every problem of the file at once and knows how many there are before reading them.
    /// </summary>
    [Fact]
    public void ARefusal_NamesTheFile_CountsItsProblems_AndListsEachOnALineOfItsOwn()
    {
        var refusal = Assert.Throws<HarnessException>(() => WorkflowFileParser.Parse(FilePath, "description: nothing\n"));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            $"'{FilePath}' has 2 problem(s):{Environment.NewLine}"
                + $"  - the file names no 'name'; a workflow's name is its directory's and its file's, said again in the file so a copied directory cannot pass as the workflow it was copied from{Environment.NewLine}"
                + "  - the file declares no 'steps'; a workflow that runs nothing is a file whose run reports success having done no work.",
            refusal.Message);

        var single = Assert.Throws<HarnessException>(() => WorkflowFileParser.Parse(FilePath, "name: default\n"));

        Assert.Equal(
            $"'{FilePath}' has 1 problem(s):{Environment.NewLine}  - the file declares no 'steps'; a workflow that runs nothing is a file whose run reports success having done no work.",
            single.Message);
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

        Assert.Equal(
            ["line 2, column 1: 'maximReasoningAgents' is not a key of the file's top level. Known: 'name', 'description', 'maxReasoningAgents', 'maxParallelProcesses', 'maxOpenAgents', 'maxRetries', 'backoffSeconds', 'maxInstances', 'pushCheckpoints', 'handoffSeconds', 'budget', 'recommendations', 'advisor', 'reporter', 'amend', 'answers', 'inputs', 'params', 'outputs', 'steps', 'recover'."],
            problems);
    }

    /// <summary>
    /// A key written twice is refused by YAML itself, as a file that does not read: the second would quietly take the
    /// first's place, and which value ran would depend on where each sat in the file. An answer's kind is such a key, so
    /// a kind answered twice is refused the same way.
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

        Assert.Equal(["line 2, column 1: the file is not valid YAML (Duplicate key name)."], problems);

        var answered = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
                answers:
                  pause: go
                  pause: stop
            """);

        Assert.Equal(["line 7, column 7: the file is not valid YAML (Duplicate key pause)."], answered);
    }

    /// <summary>
    /// The file is one YAML document whose top level is a mapping of keys: an empty file, several documents and a top
    /// level of any other shape are each refused, before any key is read.
    /// </summary>
    [Fact]
    public void TheFile_IsOneDocument_WhoseTopLevelIsAMapping()
    {
        Assert.Equal(["the file is empty."], Problems(string.Empty));
        Assert.Equal(
            ["the file holds 2 YAML documents; a workflow's definition holds one, so that nothing has to decide which of them a run ran."],
            Problems("name: default\n---\nname: other\n"));
        Assert.Equal(["line 1, column 1: the file's top level is not a mapping of keys."], Problems("- name: default\n"));
    }

    /// <summary>
    /// Each name a list of inputs, params or outputs declares is one value: a name declared twice is refused where the
    /// second sits, since what everything reads it as would otherwise depend on where it sat in the file.
    /// </summary>
    [Fact]
    public void ADeclaredName_IsDeclaredOnce()
    {
        var problems = Problems(
            """
            name: default
            inputs:
              - name: request
              - name: request
            params:
              - name: rounds
              - name: rounds
            outputs:
              - name: title
                from: 'orient.title'
              - name: title
                from: 'orient.title'
            steps:
              - name: orient
                type: orient
            """);

        Assert.Equal(
            [
                "line 4, column 5: 'inputs' names 'request' more than once; a name is one value, and a second would leave what everything reads it as depending on where it sat in the file.",
                "line 7, column 5: 'params' names 'rounds' more than once; a name is one value, and a second would leave what everything reads it as depending on where it sat in the file.",
                "line 11, column 5: 'outputs' names 'title' more than once; a name is one value, and a second would leave what everything reads it as depending on where it sat in the file.",
            ],
            problems);
    }

    /// <summary>
    /// Every section that is a mapping of keys, a list or a map of kinds is refused where it is written as another
    /// shape, naming the section and the shape it takes, with the line it was read at.
    /// </summary>
    [Fact]
    public void ASectionOfTheWrongShape_IsRefusedNamingTheShapeItTakes()
    {
        var problems = Problems(
            """
            name: default
            budget: 5
            advisor: counsel
            reporter: [teller]
            amend: later
            answers: [loopBound]
            inputs: request
            params:
              - rounds
            outputs: {}
            steps:
              - name: orient
                type: orient
                answers:
                  loopBound: [more]
            recover: none
            """);

        Assert.Equal(
            [
                "line 2, column 9: 'budget' is not a mapping of keys.",
                "line 3, column 10: 'advisor' is not a mapping of keys.",
                "line 4, column 11: 'reporter' is not a mapping of keys.",
                "line 5, column 8: 'amend' is not a mapping of keys.",
                "line 6, column 10: 'answers' is not a mapping of question kind to answer.",
                "line 7, column 9: 'inputs' is not a sequence.",
                "line 9, column 5: an entry of 'params' is not a mapping of 'name', 'description', 'default', 'required'",
                "line 10, column 10: 'outputs' is not a sequence.",
                "line 15, column 18: orient.answers.loopBound is neither the answer alone nor a mapping of 'answer', 'times', 'then'",
                "line 16, column 10: 'recover' is not a mapping of keys.",
            ],
            problems);
    }

    /// <summary>
    /// A step is a mapping that names itself with a single value, and a group's steps are a list that holds one at the
    /// least: each other shape is refused naming what is wrong, and a name or a type written as a list is never read as
    /// one left out.
    /// </summary>
    [Fact]
    public void AStep_IsAMappingNamedWithOneValue_AndAGroupHoldsSteps()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - orient
              - type: orient
              - name: [listed]
                type: orient
              - name: typed
                type: [orient]
              - name: group
                steps: []
              - name: nested
                steps: orient
            """);

        Assert.Equal(
            [
                "line 3, column 5: a step is not a mapping of keys.",
                "line 4, column 5: a step names no 'name'; what dependsOn, on, resume and expressions read would have nothing to read.",
                "line 5, column 11: a step's 'name' is not a single value.",
                "line 8, column 11: 'typed.type' is not a single value.",
                "line 10, column 12: 'steps' is empty; a group that runs nothing would end at once, and its run with it.",
                "line 12, column 12: 'steps' is not a sequence.",
            ],
            problems);

        Assert.Equal(["line 2, column 8: 'steps' is empty; a group that runs nothing would end at once, and its run with it."], Problems("name: default\nsteps: []\n"));
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

        Assert.Equal(
            [
                "line 3, column 5: loose names no type and holds no steps; a step does one thing, and a group holds steps of their own.",
                "line 4, column 5: both names the type 'orient' and holds steps; a group holds steps and names no type, and a typed step runs one thing.",
                "line 7, column 7: 'steps' is not a key of a orient step; it is one of a group, which names no type.",
            ],
            problems);
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

        Assert.Equal(["line 5, column 13: 'runner' is not a key of a test step; it is one of a run step."], problems);
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

        Assert.Equal(
            ["line 4, column 11: 'tests' is not a step type. Known: 'orient', 'reason', 'build', 'test', 'run', 'check-mutations', 'create-agent', 'fold', 'delete-agent', 'refresh', 'rebase', 'validate-plan', 'anchor-balance', 'anchor-citations', 'ci-read', 'ci-wait', 'commit', 'push', 'pull-request', 'pause'."],
            problems);
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

        Assert.Equal(
            [
                "line 3, column 5: plan names no 'prompt'; a reason step is refused without it.",
                "line 3, column 5: plan names no 'outputs'; a reason step is refused without it.",
                "'gate.failOn' is 'sometimes', which is none of 'red', 'never'.",
            ],
            problems);
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

        Assert.Equal(
            [
                "line 2, column 21: 'maxReasoningAgents' is 'four', which is not a whole number.",
                "line 6, column 16: 'orient.dependsOn' is not a list.",
            ],
            problems);
    }

    /// <summary>
    /// A key is a plain name, a single value is not a list, a flag is true or false, and a map of values is a mapping:
    /// each other shape is refused naming the key it was read for, with the line it was read at.
    /// </summary>
    [Fact]
    public void AKeyAFlagAValueAndAMap_AreEachOfTheirOwnShape()
    {
        var problems = Problems(
            """
            name: default
            description: [a, b]
            pushCheckpoints: sometimes
            ? [complex]
            : value
            steps:
              - name: corpus
                type: run
                runner: corpus
                inputs: [a]
            """);

        Assert.Equal(
            [
                "line 4, column 3: a key is not a plain name.",
                "line 2, column 14: 'description' is not a single value.",
                "line 3, column 18: 'pushCheckpoints' is 'sometimes', which is not true or false.",
                "line 10, column 13: 'corpus.inputs' is not a mapping of name to value.",
            ],
            problems);
    }

    /// <summary>
    /// A handler's on, a group's repeat, a fold's allow, a pause's pre-answers and a pre-answer's quote are each of the
    /// shape their keys say, and an on is raised by an ending there is, with its codes written as a list: every other
    /// shape is refused naming the keys it takes.
    /// </summary>
    [Fact]
    public void TheBlocksOfAStep_AreOfTheShapeTheirKeysSay()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: gate
                type: test
              - name: mend
                type: orient
                on: gate
              - name: fix
                type: orient
                on:
                  step: gate
                  outcome: broke
                  codes: 20
              - name: loop
                repeat: forever
                steps:
                  - name: inner
                    type: orient
              - name: make
                type: create-agent
                agent: implementer
              - name: fold
                type: fold
                of: make
                allow: [new]
              - name: ask
                type: pause
                question: now?
                options: [go]
                preAnswer: go
              - name: asked
                type: pause
                question: now?
                options: [go]
                preAnswer:
                  - go
                  - when: 'input.x'
                    answer: go
                    quote: words
            """);

        Assert.Equal(
            [
                "line 7, column 9: 'mend.on' is not a mapping of 'step', 'outcome', 'codes'",
                "line 13, column 14: 'fix.on.codes' is not a list.",
                "line 11, column 7: fix.on.outcome is 'broke', which is none of 'failure', 'success', 'end'.",
                "line 15, column 13: 'loop.repeat' is not a mapping of 'until', 'maxIterations', 'rounds', 'blocking', 'blockersLimitRounds', 'leftover'",
                "line 25, column 12: 'fold.allow' is not a mapping of 'settled', 'new', 'acceptLost', 'merge', 'acceptOver'",
                "line 30, column 16: 'ask.preAnswer' is not a sequence.",
                "line 36, column 9: an entry of 'asked.preAnswer' is not a mapping of 'when', 'answer', 'text', 'quote'",
                "line 39, column 16: 'asked.preAnswer.quote' is not a mapping of 'text', 'input'",
            ],
            problems);
    }

    /// <summary>
    /// An input's kind is text or file, a file input takes no options - it is a path, checked when the run starts - and
    /// options given empty are refused rather than read as any value, which is what leaving the key out says.
    /// </summary>
    [Fact]
    public void AnInputsKindAndOptions_AreOnesItCanTake()
    {
        var problems = Problems(
            """
            name: default
            inputs:
              - name: plan
                kind: folder
              - name: spec
                kind: file
                options: [a]
              - name: tone
                options: []
            steps:
              - name: orient
                type: orient
            """);

        Assert.Equal(
            [
                "line 3, column 5: inputs.plan.kind is 'folder', which is neither of text nor file.",
                "line 5, column 5: inputs.spec declares options and the kind 'file': options list what a 'text' input takes, and a file input is a path, checked when the run starts.",
                "line 9, column 14: 'inputs.tone.options' is given empty, which is never read as any value: list the values it takes, or leave the key out for any value.",
            ],
            problems);
    }

    /// <summary>
    /// A list given empty is never read as the key left out, which says something else: a pause's pre-answers given empty
    /// are not "no answer given beforehand", and the checks a wait ignores given empty are not "every check counted".
    /// </summary>
    [Fact]
    public void AListGivenEmpty_IsRefusedRatherThanReadAsTheKeyLeftOut()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: approval
                type: pause
                question: approve?
                options: [approve]
                preAnswer: []
              - name: checks
                type: ci-wait
                ignore: []
            """);

        Assert.Equal(
            [
                "line 7, column 16: 'approval.preAnswer' is given empty, which is never read as no answer given beforehand: list the answers a person may give with the run, or leave the key out for no answer given beforehand.",
                "line 10, column 13: 'checks.ignore' is given empty, which is never read as every check counted: name the checks it does not count, or leave the key out for every check counted.",
            ],
            problems);
    }

    /// <summary>
    /// V43: the kinds an advisor recommends for are given not empty - an advisor that names none recommends for every
    /// kind, which kinds given empty would never be read as.
    /// </summary>
    [Fact]
    public void AnAdvisorsKindsGivenEmpty_AreRefusedRatherThanReadAsEveryKind()
    {
        Assert.Equal(
            ["line 5, column 10: 'advisor.kinds' is given empty, which is never read as every kind: name the kinds it recommends for, or leave the key out for every kind."],
            Problems(
                """
                name: default
                advisor:
                  agent: advisor
                  prompt: prompts/recommend.md
                  kinds: []
                steps:
                  - name: orient
                    type: orient
                """));
    }

    /// <summary>
    /// V44: everything about a plan update is said by the workflow's amend block, and no step carries a key of one: such a
    /// key on a step is refused saying where it belongs, where any other key a step does not take lists those it does.
    /// </summary>
    [Fact]
    public void APlanUpdatesKeyOnAStep_IsRefusedSayingItIsAmends()
    {
        Assert.Equal(
            [
                "line 5, column 5: 'whenAddScope' is not a key of a step; it is a key of amend, at the top of the file, the one place a plan update is said.",
                "line 6, column 5: 'changedAnswerMinutes' is not a key of a step; it is a key of amend, at the top of the file, the one place a plan update is said.",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: replan
                    type: orient
                    whenAddScope: items.build
                    changedAnswerMinutes: 1
                """));
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

        // The tenth level's own steps are where the bound is passed: one line past each level's name, two below it.
        var line = (WorkflowFileParser.MaxDepth * 2) + 3;
        var column = (WorkflowFileParser.MaxDepth * 2) + 3;

        Assert.Equal(
            [$"line {line}, column {column}: step-{WorkflowFileParser.MaxDepth} holds steps {WorkflowFileParser.MaxDepth + 1} levels deep; steps nest at most {WorkflowFileParser.MaxDepth}."],
            problems);
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
    /// A budget is a number a run can count down - never NaN or infinity, and its tokens a whole number; a loop past its rounds may be bounded by
    /// nothing but the budget, read as false; the advisor and the reporter each name the agent they run, which is
    /// required; and the recovery holds steps and says where the run goes once they are done, back to the step that
    /// failed, and what a failure of its own work does - a value that is no policy at all is refused naming the two a
    /// recovery may say.
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
              tokens: lots
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
                "line 5, column 11: 'budget.tokens' is 'lots', which is not a whole number.",
                "'advisor.agent' is named nowhere; it is required.",
                "'reporter.agent' is named nowhere; it is required.",
                "line 13, column 14: 'recover.onSuccess' is 'elsewhere', which is none of 'failed-step'; a recovery sends the run back to the step that gave it its failure, to run again once it was put right.",
                "line 14, column 14: 'recover.onFailure' is 'retry', which is none of 'pause', 'stop'; a failure the recovery's own work meets asks a person, or ends the run.",
                "line 12, column 3: 'recover' holds no 'steps'; a recovery that runs nothing would put nothing right, and the step that failed would run again unchanged.",
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

        // A step's other policies are read here, for the validator to refuse in words of their own.
        foreach (var policy in new[] { "continue", "recover" })
        {
            var read = Parse(
                $"""
                name: default
                steps:
                  - name: orient
                    type: orient
                recover:
                  onSuccess: failed-step
                  onFailure: {policy}
                  steps:
                    - name: diagnose
                      type: orient
                """);

            Assert.Equal(policy, read!.Recover!.OnFailure);
        }
    }

    /// <summary>
    /// V29: what only a person gives - the paths a fold or a rebase leaves out as settled by hand, and the lost and over
    /// cells a fold accepts - is written out, never read from an output: a single value under one of those keys is an
    /// expression, refused in the rule's own words, never told that a list is missing. Anything else that is not a
    /// list is refused as any list is.
    /// </summary>
    [Fact]
    public void WhatOnlyAPersonGives_IsWrittenOut_NeverReadFromAnOutput()
    {
        const string Rule = "from an output: paths settled by hand, and lost and over cells, are a person's to accept; only new rows and merges may be read from one.";

        Assert.Equal(
            [
                $"line 10, column 16: step 'close' takes allow.settled {Rule}",
                $"line 11, column 19: step 'close' takes allow.acceptLost {Rule}",
                $"line 12, column 19: step 'close' takes allow.acceptOver {Rule}",
                $"line 16, column 14: step 'move' takes settled {Rule}",
            ],
            Problems(
                """
                name: flow
                steps:
                  - name: make
                    type: create-agent
                    agent: worker
                  - name: close
                    type: fold
                    of: make
                    allow:
                      settled: plan.latest.paths
                      acceptLost: plan.latest.lost
                      acceptOver: plan.latest.over
                  - name: move
                    type: rebase
                    of: make
                    settled: plan.latest.paths
                """));

        Assert.Equal(
            ["line 10, column 16: 'close.allow.settled' is not a list."],
            Problems(
                """
                name: flow
                steps:
                  - name: make
                    type: create-agent
                    agent: worker
                  - name: close
                    type: fold
                    of: make
                    allow:
                      settled: { paths: src }
                """));
    }
}
