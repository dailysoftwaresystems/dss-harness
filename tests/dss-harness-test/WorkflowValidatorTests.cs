using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The structural rules a parsed definition must hold: every name a step, an expression or a template reads is
/// declared, and every key means what its step can do with it - checked before anything runs.
/// </summary>
public sealed class WorkflowValidatorTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests are of the rules over one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/default/default.yml", text);

    private static IReadOnlyList<string> Problems(string text)
        => [.. WorkflowValidator.Validate(Parse(text))];

    /// <summary>
    /// An expression reads its own siblings, the siblings of the groups holding it, and steps by their path from the
    /// root - and nothing else. The shipped default flow's own expressions are the shape of this.
    /// </summary>
    [Fact]
    public void AnExpression_ReadsSiblingsAncestorSiblingsAndRootPaths_AndNothingElse()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
              - name: intake
                steps:
                  - name: triage
                    type: reason
                    agent: triage
                    prompt: prompts/triage.md
                    outputs: schemas/triage.json
                gather: [stops]
              - name: blocked
                type: pause
                dependsOn: [intake]
                when: 'intake.gathered && orient.anything'
                question: 'waits on {intake.stops} and {later.step}'
                options: [continue]
              - name: later
                type: orient
            """);

        Assert.Equal(
            ["'blocked.question' reads 'later.step', which no step before 'blocked', no input and no param provides"],
            problems);
    }

    /// <summary>
    /// The run's own names are read where they are declared: an input, a param, the work item of a forEach, the
    /// previous iteration of a repeat - its own group's as much as a member's - and what a commit step checked, each
    /// only where it can be.
    /// </summary>
    [Fact]
    public void TheRunsOwnNames_AreReadOnlyWhereTheyAreDeclared()
    {
        var problems = Problems(
            """
            name: default
            inputs:
              - name: request
            params:
              - name: rounds
                default: '3'
            steps:
              - name: check
                type: validate-plan
                items: 'param.rounds'
              - name: itemised
                forEach: 'check.items'
                steps:
                  - name: build
                    type: orient
                    when: 'item.owned && param.rounds'
              - name: looped
                repeat:
                  until: 'previous.self.ok'
                  maxIterations: 3
                steps:
                  - name: self
                    type: orient
              - name: wrong
                type: orient
                when: 'item.owned || previous.self.ok || input.missing || param.absent || run.ran'
              - name: commit
                type: commit
                dependsOn: [wrong]
                message: 'carried: {carried.red}'
              - name: said
                type: pull-request
                dependsOn: [commit]
                title: 'carried again: {carried.red}'
                body: prompts/pr-body.md
            """);

        Assert.Equal(
            [
                "'wrong.when' reads 'item.owned', whose 'item' names the work item of a forEach group, and no group holding 'wrong' runs one",
                "'wrong.when' reads 'previous.self.ok', whose 'previous' names the iteration of a repeat group before this one, and no group holding 'wrong' runs one",
                "'wrong.when' reads 'input.missing', and the workflow declares no input 'missing'",
                "'wrong.when' reads 'param.absent', and the workflow declares no param 'absent'",
                "'said.title' reads 'carried.red', whose 'carried' says what the checks upstream of a commit checked, and 'said' is no commit step",
            ],
            problems);
    }

    /// <summary>
    /// A dependency is a sibling declared before the step; one declared after it, one of another group, and the step
    /// itself are each refused, each in their own words. Depending on a handler is not: a handler the ending it answers
    /// to never raised satisfies it, as the shipped flow's folds depend on their menders.
    /// </summary>
    [Fact]
    public void ADependency_IsAnEarlierSibling()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
              - name: gate
                type: test
                dependsOn: [release]
              - name: release
                type: orient
                dependsOn: [release]
              - name: mender
                type: reason
                agent: mender
                prompt: prompts/mend.md
                outputs: schemas/mend.json
                on:
                  step: gate
                  outcome: failure
                then: gate
              - name: waiter
                type: orient
                dependsOn: [mender]
              - name: stranger
                type: orient
                dependsOn: [elsewhere.orient]
            """);

        Assert.Equal(
            [
                "step 'gate' depends on 'release', which is declared after it; steps are declared in the order they run, and a dependency declared after the step that waits for it is met never or too late",
                "step 'release' depends on itself; a step waits for what runs before it, and this one would wait for itself",
                "step 'stranger' depends on 'elsewhere.orient', which is no step declared beside it; a step waits for its own siblings, and a step of another group is waited for by the group that holds it",
            ],
            problems);
    }

    /// <summary>
    /// A reason step writes in a worktree a create-agent step made, goes on with the work of a reason step or its own,
    /// and cannot be independent of the very work it resumes.
    /// </summary>
    [Fact]
    public void AReasonStep_WritesWhereAnAgentWasMade_AndGoesOnWithItsOwnOrAnotherReasonStepsWork()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: make
                type: create-agent
                agent: implementer
              - name: implement
                type: reason
                agent: implementer
                prompt: prompts/implement.md
                outputs: schemas/implement.json
                access: write
                in: make
              - name: reads-but-names-in
                type: reason
                agent: reader
                prompt: prompts/read.md
                outputs: schemas/read.json
                in: make
              - name: resumes
                type: reason
                prompt: prompts/mend.md
                outputs: schemas/mend.json
                resume: implement
                agent: mender
                independent: true
              - name: makes-no-agent
                type: reason
                prompt: prompts/x.md
                outputs: schemas/x.json
              - name: folding
                type: fold
                of: implement
              - name: folding-wrong
                type: fold
                of: reads-but-names-in
            """);

        Assert.Equal(
            [
                "step 'reads-but-names-in' reads and names 'in'; a reading step has no worktree to work in, and reads the main tree",
                "step 'resumes' names the agent 'mender' and resumes 'implement'; a step that goes on with another's work runs in that step's agent, and cannot both go on with another's work and name another agent",
                "step 'resumes' is independent and resumes 'implement'; independence is never being given the author's work, and resuming is going on with exactly that",
                "step 'makes-no-agent' names no agent and resumes no step; a reason step runs an agent of its own, or goes on with the agent of the step it resumes",
                "step 'folding' names 'implement', which is a reason step; an agent is made by a create-agent step",
                "step 'folding-wrong' names 'reads-but-names-in', which is a reason step; an agent is made by a create-agent step",
            ],
            problems);
    }

    /// <summary>
    /// A group loops one way - once for each work item, or again and again until a bound - gathers as a block, and a
    /// repeat's rounds come with what keeps the loop going past them.
    /// </summary>
    [Fact]
    public void AGroup_LoopsOneWay_GathersAsABlock_AndRepeatsWithAWholeBound()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: plan
                type: reason
                agent: planner
                prompt: prompts/plan.md
                outputs: schemas/plan.json
              - name: check
                type: validate-plan
                items: 'plan.items'
              - name: both
                forEach: 'check.items'
                repeat:
                  until: 'check.ok'
                  maxIterations: 3
                gather: [findings]
                steps:
                  - name: inner
                    type: orient
              - name: unbouned
                repeat:
                  until: 'check.ok'
                steps:
                  - name: inner
                    type: orient
              - name: roundless
                repeat:
                  until: 'check.ok'
                  maxIterations: 3
                  blocking: 'any(check.findings, it.severity != ''low'')'
                steps:
                  - name: inner
                    type: orient
              - name: blockless
                repeat:
                  until: 'check.ok'
                  rounds: 3
                steps:
                  - name: inner
                    type: orient
            """);

        Assert.Equal(
            [
                "group 'both' says both forEach and repeat; a group runs its steps once for each work item, or again and again until its bound is met, and a group that did both would never tell which it was doing",
                "group 'both' gathers and loops; gather is a block's, which runs its steps once and gathers what they wrote into one list",
                "group 'unbouned' repeats with no bound; give maxIterations, or rounds with blocking, so the loop always has an end",
                "group 'roundless' says blocking without rounds; past the rounds is the only place either is read",
                "group 'blockless' repeats in rounds and says no blocking; blocking is what says, past the rounds, whether what is left must still be dealt with, and a loop without it would end with the rounds whatever was left",
            ],
            problems);
    }

    /// <summary>
    /// A step is named as everything reads it - a lower-case word, no reserved word, one to a sibling list - and a
    /// declaration that a default always supplies is never also required.
    /// </summary>
    [Fact]
    public void StepsAndDeclarations_AreNamedAsEverythingReadsThem()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
              - name: Uppercase
                type: orient
              - name: latest
                type: orient
              - name: orient
                type: orient
            inputs:
              - name: request
                options: [yes, 'no']
                default: maybe
                required: true
            params:
              - name: fine
            outputs:
              - name: title
                from: 'orient.anything'
            """);

        Assert.Equal(
            [
                "input 'request' is required and declares a default: a default always supplies the value, so required would never refuse. Give the default, or require the value.",
                "input 'request' defaults to 'maybe', which is not one of its options ('yes', 'no'); a run that used the default would be refused for the value the file itself gave",
                "step 'Uppercase' is named 'Uppercase', which is not a lower-case word with dashes within; a read's segments are told apart by dots, so a name may not hold one",
                "step 'latest' is named 'latest', a word a definition reads for its own; a read could not tell the step from the word",
                "two steps of 'the flow's root' are named 'orient'; what depends on it, or reads it, could only ever mean one of them",
            ],
            problems);
    }

    /// <summary>
    /// A person's pause offers what it takes and is never answered beforehand; another's may be, and the answer's kind
    /// is one the engine raises.
    /// </summary>
    [Fact]
    public void APersonsPause_IsNeverAnsweredByAConfiguration_AndAKindIsOneTheEngineRaises()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
              - name: asked
                type: pause
                question: what now?
                person: true
                answers:
                  pause: continue
              - name: offered
                type: pause
                dependsOn: [asked]
                question: and now?
                options: [go, stop]
                text: [halt]
            answers:
              unboundLoop: accept
            """);

        Assert.Equal(
            [
                "the workflow's answers answers the kind 'unboundLoop', which is no question kind. Known: 'loopBound', 'visitBound', 'openAgents', 'foldNewRows', 'foldLostRows', 'foldOverCells', 'foldTwoChanges', 'foldConflicts', 'foldOutside', 'turnLimit', 'truncated', 'stepBudget', 'contextLimit', 'runBudget', 'noChecks', 'checksUnsettled', 'checksPending', 'failure', 'pause', 'ask'.",
                "step 'asked' offers no options; a question with nothing to answer leaves a person typing into a run that reads nothing",
                "step 'asked' is a person's and answers its own question beforehand; what only a person may answer, no line of a configuration answers",
                "step 'offered' says the option 'halt' takes a text, and offers no such option",
            ],
            problems);
    }

    /// <summary>
    /// The amend block names steps of the flow and an input the workflow declares, and its planner is a reason step.
    /// </summary>
    [Fact]
    public void AnAmendBlockNamesStepsAndAnInput()
    {
        var problems = Problems(
            """
            name: default
            inputs:
              - name: request
            steps:
              - name: orient
                type: orient
              - name: replan
                type: reason
                agent: planner
                prompt: prompts/update.md
                outputs: schemas/update.json
            amend:
              input: ask
              before: orient
              step: orient
              scope: 'replan.scope'
              whenAddScope: missing-add
              whenChangeScope: missing-change
              whenRemoveScope: missing-remove
            """);

        Assert.Equal(
            [
                "amend.input names 'ask', which the workflow does not declare as an input",
                "amend.step names 'orient', a orient step; a plan is updated by a reasoning agent, and no other step holds the conversation it is updated in",
                "amend.whenAddScope names 'missing-add', which is no step of the flow",
                "amend.whenChangeScope names 'missing-change', which is no step of the flow",
                "amend.whenRemoveScope names 'missing-remove', which is no step of the flow",
            ],
            problems);
    }

    /// <summary>
    /// Where the configuration declares a commit template, a commit step says the values it reads and its own message
    /// is refused; where none is declared, the message is required.
    /// </summary>
    [Fact]
    public void ACommitStep_WritesTheMessageTheConfigurationLeavesIt()
    {
        var text =
            """
            name: default
            steps:
              - name: commit
                type: commit
            """;

        var withoutTemplate = Problems(text);

        Assert.Equal(["step 'commit' names no message and the configuration declares no commit.template; one of the two writes the commit's message"], withoutTemplate);

        var withTemplate = WorkflowValidator.Validate(Parse(text), hasCommitTemplate: true);

        Assert.Empty(withTemplate);
    }

    /// <summary>
    /// A handler is raised by a sibling's ending, says then where it sends the run back to, and its visits are whole;
    /// then without on is a step the schedule starts with nowhere to send anything.
    /// </summary>
    [Fact]
    public void AHandler_IsRaisedByASiblingEnding_AndSendsTheRunSomewhereItCan()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: gate
                type: test
              - name: mender
                type: reason
                agent: mender
                prompt: prompts/mend.md
                outputs: schemas/mend.json
                on:
                  step: gate
                  outcome: failure
                  codes: [0, 20]
                then: elsewhere
                maxVisits: 0
              - name: sends
                type: reason
                agent: sender
                prompt: prompts/mend.md
                outputs: schemas/mend.json
                then: gate
            """);

        Assert.Equal(
            [
                "step 'mender' sends the run to 'elsewhere', which is no step declared beside it",
                "step 'mender': maxVisits is 0, and so not one or more",
                "step 'mender' is raised by the codes 0, 20, and a code below 1 is no code",
                "step 'sends' says then without on; then is where a handler sends the run when it is done, and a step the schedule starts has nothing to send back",
            ],
            problems);
    }
}
