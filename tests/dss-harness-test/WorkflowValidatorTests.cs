using System.Text;
using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The structural rules a parsed definition must hold: every name a step, an expression or a template reads is
/// declared, and every key means what its step can do with it - checked before anything runs.
/// </summary>
public sealed class WorkflowValidatorTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests are of the rules over one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/implementation/implementation.yml", text);

    /// <summary>Every problem of a definition read under a configuration that declares no commit template, as most are.</summary>
    private static IReadOnlyList<string> Problems(string text)
        => [.. WorkflowValidator.Validate(Parse(text), hasCommitTemplate: false)];

    /// <summary>
    /// V51: a reasoning step's pre-existing issues go to a step of the flow's top level raised by them - never scheduled,
    /// as the recovery is not - which gives one it decides to fix to the recovery. A workflow wired so holds; and where it
    /// has such a step, a reasoning step that names none is refused, so no issue a step meets is lost. The recovery is
    /// reached by the triage's onAddress alone.
    /// </summary>
    [Fact]
    public void PreExistingIssues_GoToARaisedTriage_ThatSendsAFixToTheRecovery_AndEveryReasoningStepNamesIt()
    {
        Assert.Empty(Problems(PreExisting("")));

        Assert.Equal(
            ["step 'other' says no onPreExisting while 'triage-pre-existing-issue' triages pre-existing issues; every reasoning step names the step its issues go to, so none it meets is lost"],
            Problems(PreExisting(
                """
                  - name: other
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                """)));
    }

    /// <summary>
    /// The issue a step reported is read by the step raised by it and by the recovery that fixes one - with the step
    /// that reported it, as source - and by nothing else.
    /// </summary>
    [Fact]
    public void AReportedIssue_IsReadByItsTriageAndTheRecovery_AndNothingElse()
    {
        Assert.Equal(
            ["'plan.when' reads 'issue.title', whose 'issue' is the pre-existing issue a step reported, and 'plan' is neither raised by one nor a step of the recovery that fixes one"],
            Problems(
                """
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                    when: "!empty(issue.title)"
                    onPreExisting: triage-pre-existing-issue
                  - name: triage-pre-existing-issue
                    type: reason
                    agent: pre-existing-triage
                    prompt: prompts/pre-existing.md
                    outputs: schemas/pre-existing.json
                    raised: preExisting
                    onAddress: recover
                recover:
                  onSuccess: failed-step
                  onFailure: pause
                  steps:
                    - name: diagnose
                      type: orient
                      when: "!empty(issue.title) && !empty(source.title)"
                """));
    }

    /// <summary>
    /// V51: each way a pre-existing issue's wiring can be wrong is refused by name - an onPreExisting that names no step
    /// of the top level, or one not raised by an issue; a raised step that is scheduled after all (it waits, is waited
    /// for, says on, when or always), sits in a group, sends a fix nowhere, or sends its own issues to be triaged, which
    /// is it calling itself; and onAddress where nothing raised the step.
    /// </summary>
    [Fact]
    public void APreExistingIssuesWiring_ThatIsWrong_IsRefusedEachWay()
    {
        Assert.Equal(
            [
                "step 'orient' says onPreExisting: 'plan', which says no raised: preExisting; the step that triages a reported issue is raised by it, never scheduled",
                "step 'plan' says onPreExisting: 'ghost', which is no step of the flow's top level; the step that triages a reported issue is one the whole flow names",
                "step 'scheduled' says onPreExisting: 'orient', which says no raised: preExisting; the step that triages a reported issue is raised by it, never scheduled",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: orient
                    type: reason
                    agent: triage
                    prompt: prompts/triage.md
                    outputs: schemas/triage.json
                    onPreExisting: plan
                  - name: plan
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                    onPreExisting: ghost
                  - name: scheduled
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                    onPreExisting: orient
                """));

        Assert.Equal(
            [
                "step 'plan' says onAddress and is not raised by a pre-existing issue; onAddress says where a step raised by one sends an issue it decides to fix",
                "step 'triage-pre-existing-issue' is raised by a pre-existing issue and also depends on 'plan'; a raised step waits for the issue it is given and for nothing else",
                "step 'triage-pre-existing-issue' is raised by a pre-existing issue, and 'after' depends on it; a raised step is never scheduled, so nothing waits for it",
                "step 'triage-pre-existing-issue' is raised by a pre-existing issue and says when; every issue raised to it is triaged",
                "step 'triage-pre-existing-issue' is raised by a pre-existing issue and says no onAddress; an issue it decides to fix goes to the recovery: say onAddress: recover",
                "step 'triage-pre-existing-issue' is raised by a pre-existing issue and sends the issues it meets to be triaged (onPreExisting), and so calls itself; a step never does - one that is to run again after it failed says retries",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                    onPreExisting: triage-pre-existing-issue
                    onAddress: recover
                  - name: triage-pre-existing-issue
                    type: reason
                    agent: pre-existing-triage
                    prompt: prompts/pre-existing.md
                    outputs: schemas/pre-existing.json
                    raised: preExisting
                    dependsOn: [plan]
                    when: "plan.go"
                    onPreExisting: triage-pre-existing-issue
                  - name: after
                    type: orient
                    dependsOn: [triage-pre-existing-issue]
                """));

        Assert.Equal(
            [
                "step 'items.inner' says raised: preExisting and is not a step of the flow's top level; the step that triages a reported issue is one every step of the flow can name",
                "step 'items.inner' says onAddress: recover, and the workflow declares no recover; an issue to fix at its root is the recovery's work",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: items
                    steps:
                      - name: inner
                        type: reason
                        agent: pre-existing-triage
                        prompt: prompts/pre-existing.md
                        outputs: schemas/pre-existing.json
                        raised: preExisting
                        onAddress: recover
                """));
    }

    /// <summary>
    /// What waits for a raised step is found as every dependency is (<c>FlowScope</c>): <c>dependsOn</c> names the
    /// sibling of that name, the first declared where siblings share one. Beside a scheduled step of the same name, a
    /// raised one declared after it is waited for by nothing - only the shared name is refused - and a reasoning step
    /// sends its issues to the first of the two, which says no raised.
    /// </summary>
    [Fact]
    public void WhatWaitsForARaisedStep_IsFoundAsADependencyIs()
    {
        Assert.Equal(
            [
                "two steps of 'the flow's root' are named 'triage-issue'; what depends on it, or reads it, could only ever mean one of them",
                "step 'triage' says onPreExisting: 'triage-issue', which says no raised: preExisting; the step that triages a reported issue is raised by it, never scheduled",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: triage
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                    onPreExisting: triage-issue
                  - name: triage-issue
                    type: orient
                  - name: triage-issue
                    type: reason
                    agent: pre-existing-triage
                    prompt: prompts/pre-existing.md
                    outputs: schemas/pre-existing.json
                    raised: preExisting
                    onAddress: recover
                  - name: after
                    type: orient
                    dependsOn: [triage-issue]
                recover:
                  onSuccess: failed-step
                  onFailure: pause
                  steps:
                    - name: diagnose
                      type: orient
                """));
    }

    /// <summary>
    /// V51: a raised step is raised one way - never also by another step's ending, never always - and no handler sends the
    /// run to it; it reads the issue it is given and the step that reported it, as the recovery does.
    /// </summary>
    [Fact]
    public void ARaisedStep_IsRaisedOneWay_NoHandlerSendsTheRunToIt_AndItReadsItsIssue()
    {
        Assert.Equal(
            [
                "step 'triage-pre-existing-issue' is raised by 'plan' and is always; an always step is started by the end of the session, never by a step",
                "step 'triage-pre-existing-issue' is raised by a pre-existing issue and by another step's ending (on); a step is raised one way",
                "step 'triage-pre-existing-issue' is raised by a pre-existing issue and is always; a step runs at the session's end or for an issue, never both",
                "step 'redo' sends the run to 'triage-pre-existing-issue', which is raised by a pre-existing issue and never scheduled; nothing sends the run to it",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                    onPreExisting: triage-pre-existing-issue
                  - name: triage-pre-existing-issue
                    type: reason
                    agent: pre-existing-triage
                    prompt: prompts/pre-existing.md
                    outputs: schemas/pre-existing.json
                    raised: preExisting
                    onAddress: recover
                    on:
                      step: plan
                      outcome: failure
                    always: true
                  - name: redo
                    type: orient
                    on:
                      step: plan
                      outcome: failure
                    then: triage-pre-existing-issue
                recover:
                  onSuccess: failed-step
                  onFailure: pause
                  steps:
                    - name: diagnose
                      type: orient
                """));

        // Its own reads of the issue and of the step that reported it are refused by nothing: the one refusal is of the
        // when it may not say.
        Assert.Equal(
            ["step 'triage-pre-existing-issue' is raised by a pre-existing issue and says when; every issue raised to it is triaged"],
            Problems(PreExisting("").Replace(
                "    raised: preExisting\n",
                "    raised: preExisting\n    when: \"!empty(issue.title) && !empty(source.step)\"\n",
                StringComparison.Ordinal)));
    }

    /// <summary>
    /// A workflow whose reasoning steps send their pre-existing issues to a raised triage that gives a fix to the
    /// recovery, with <paramref name="more"/> steps after it.
    /// </summary>
    private static string PreExisting(string more)
        => $"""
            name: default
            steps:
              - name: plan
                type: reason
                agent: planner
                prompt: prompts/plan.md
                outputs: schemas/plan.json
                onPreExisting: triage-pre-existing-issue
              - name: triage-pre-existing-issue
                type: reason
                agent: pre-existing-triage
                prompt: prompts/pre-existing.md
                outputs: schemas/pre-existing.json
                raised: preExisting
                onAddress: recover
            {more}
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: orient
            """;

    /// <summary>
    /// An expression reads its own siblings, the siblings of the groups holding it, and steps by their path from the
    /// root - and nothing else. The shipped implementation flow's own expressions are the shape of this.
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
                when: '!empty(intake.stops) && orient.hasLegs'
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

        // Each is read where it is declared, and typed as what it is: a param is a text, and so is no list and no boolean.
        Assert.Equal(
            [
                "'check.items' yields a text, where a list is read: the plan check checks the work items it holds",
                "'itemised.forEach' yields a text, where a list is read: the group runs its steps once for each of its items",
                "'itemised.build.when' joins 'param.rounds', which is a text, with &&; && joins booleans, and nothing is read as true or as false but true and false",
                "'looped.repeat.until' reads 'previous.self.ok', and the output of step 'looped.self', an orient step, has no property 'ok'",
                "'wrong.when' reads 'item.owned', whose 'item' names the work item of a forEach group, and no group holding 'wrong' runs one",
                "'wrong.when' reads 'previous.self.ok', whose 'previous' names the iteration of a repeat group before this one, and no group holding 'wrong' runs one",
                "'wrong.when' reads 'input.missing', and the workflow declares no input 'missing'",
                "'wrong.when' reads 'param.absent', and the workflow declares no param 'absent'",
                "'said.title' reads 'carried.red', whose 'carried' says what the checks upstream of a commit checked, and 'said' is no commit step",
            ],
            problems);
    }

    /// <summary>
    /// A dependency is a sibling declared before the step that is no always step; one declared after it, one of another
    /// group, the step itself and an always step - started by the end of the session, which nothing waits for - are each
    /// refused, each in their own words. Depending on a handler is not: a handler the ending it answers to never raised
    /// satisfies it, as the shipped flow's folds depend on their menders.
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
              - name: report
                type: orient
                always: true
              - name: after-report
                type: orient
                dependsOn: [report]
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
              - name: twice
                type: orient
                dependsOn: [orient, orient]
            """);

        Assert.Equal(
            [
                "step 'after-report' depends on 'report', an always step; an always step is started by the end of the session, and no step waits for it",
                "step 'gate' depends on 'release', which is declared after it; steps are declared in the order they run, and a dependency declared after the step that waits for it is met never or too late",
                "step 'release' depends on itself; a step waits for what runs before it, and this one would wait for itself",
                "step 'stranger' depends on 'elsewhere.orient', which is no step declared beside it; a step waits for its own siblings, and a step of another group is waited for by the group that holds it",
                "step 'twice' depends on 'orient' more than once",
            ],
            problems);
    }

    /// <summary>
    /// A reason step writes in a worktree a create-agent step made before it, goes on with the work of a reason step
    /// declared before it or with its own, and cannot be independent of the very work it resumes; a step that only reads
    /// names no worktree, and only a step that asks says how often.
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
              - name: writes-nowhere
                type: reason
                agent: implementer
                prompt: prompts/implement.md
                outputs: schemas/implement.json
                access: write
              - name: writes-early
                type: reason
                agent: implementer
                prompt: prompts/implement.md
                outputs: schemas/implement.json
                access: write
                in: made-later
              - name: lost
                type: reason
                prompt: prompts/mend.md
                outputs: schemas/mend.json
                resume: nowhere
              - name: off-an-agent
                type: reason
                prompt: prompts/mend.md
                outputs: schemas/mend.json
                resume: make
              - name: early
                type: reason
                prompt: prompts/mend.md
                outputs: schemas/mend.json
                resume: thought-later
              - name: quiet
                type: reason
                agent: reader
                prompt: prompts/read.md
                outputs: schemas/read.json
                askRounds: 2
              - name: made-later
                type: create-agent
                agent: implementer
              - name: thought-later
                type: reason
                agent: thinker
                prompt: prompts/think.md
                outputs: schemas/think.json
            """);

        Assert.Equal(
            [
                "step 'reads-but-names-in' reads and names 'in'; a reading step has no worktree to work in, and reads the main tree",
                "step 'resumes' resumes 'implement' and names agent; a step that goes on with another's work runs in that step's agent, on its model and in its worktree, and names none of agent, client, tier, model, access, in or writes",
                "step 'resumes' is independent and resumes 'implement'; independence is never being given the author's work, and resuming is going on with exactly that",
                "step 'makes-no-agent' names no agent and resumes no step; a reason step runs an agent of its own, or goes on with the agent of the step it resumes",
                "step 'folding' names 'implement' as its of, which is a reason step; an agent is made by a create-agent step",
                "step 'folding-wrong' names 'reads-but-names-in' as its of, which is a reason step; an agent is made by a create-agent step",
                "step 'writes-nowhere' writes and names no 'in'; a writing step works in an agent's worktree, and the create-agent step that made it is the one to name",
                "step 'writes-early' works in 'made-later', which is declared after it; a worktree cannot be worked in before it is made",
                "step 'lost' resumes 'nowhere', which is no step an enclosing group declares",
                "step 'off-an-agent' resumes 'make', which is a create-agent step; only a reason step leaves a conversation to go on with",
                "step 'early' resumes 'thought-later', which is declared after it; work cannot be gone on with before it is begun",
                "step 'quiet' says askRounds without ask; how often a step may ask is a step that asks's own bound",
            ],
            problems);
    }

    /// <summary>
    /// A step goes on with its own work only where it runs again - inside a repeat, where a handler's then sends the run
    /// back to it, or in the recovery - and only such a step has a resume prompt to be asked with when it does. A step
    /// that goes on with another's work runs in that step's agent, on its model and in its worktree, and so names none of
    /// them, nor what it writes.
    /// </summary>
    [Fact]
    public void AStepGoesOnWithItsOwnWork_OnlyWhereItRunsAgain_AndWithAnothersNamingNothingOfItsOwn()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: make
                type: create-agent
                agent: implementer
              - name: once
                type: reason
                agent: thinker
                resume: once
                prompt: prompts/think.md
                resumePrompt: prompts/again.md
                outputs: schemas/think.json
              - name: looped
                repeat:
                  until: 'again.ok'
                  maxIterations: 2
                steps:
                  - name: again
                    type: reason
                    agent: thinker
                    resume: again
                    prompt: prompts/think.md
                    resumePrompt: prompts/again.md
                    outputs: schemas/think.json
              - name: sent-back
                type: reason
                agent: thinker
                resume: sent-back
                prompt: prompts/think.md
                outputs: schemas/think.json
              - name: gate
                type: test
                dependsOn: [sent-back]
                onFailure: recover
              - name: mend
                type: orient
                on:
                  step: gate
                  outcome: failure
                then: sent-back
              - name: builder
                type: reason
                agent: implementer
                access: write
                in: make
                prompt: prompts/implement.md
                outputs: schemas/implement.json
              - name: borrows
                type: reason
                resume: builder
                agent: other
                client: cli
                tier: best
                access: write
                in: make
                writes: [src/**]
                prompt: prompts/mend.md
                resumePrompt: prompts/again.md
                outputs: schemas/implement.json
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: reason
                  agent: diagnostician
                  resume: diagnose
                  prompt: prompts/diagnose.md
                  resumePrompt: prompts/rediagnose.md
                  outputs: schemas/diagnosis.json
            """);

        Assert.Equal(
            [
                "step 'once' resumes itself and runs once; a step goes on with its own work only where it runs again - inside a repeat, where a handler's then sends the run back to it, or as a step of the recovery",
                "step 'borrows' resumes 'builder' and names agent, client, tier, access, in, writes; a step that goes on with another's work runs in that step's agent, on its model and in its worktree, and names none of agent, client, tier, model, access, in or writes",
                "step 'borrows' says resumePrompt and does not resume itself; resumePrompt is what a step that goes on with its own work is asked with when it runs again, and any other step is asked with its prompt",
            ],
            problems);

        // The model is one of the keys too, named alone where the step names no tier beside it.
        Assert.Equal(
            ["step 'borrows' resumes 'builder' and names model; a step that goes on with another's work runs in that step's agent, on its model and in its worktree, and names none of agent, client, tier, model, access, in or writes"],
            Problems(
                """
                name: default
                steps:
                  - name: builder
                    type: reason
                    agent: implementer
                    prompt: prompts/implement.md
                    outputs: schemas/implement.json
                  - name: borrows
                    type: reason
                    resume: builder
                    model: some-model
                    prompt: prompts/mend.md
                    outputs: schemas/implement.json
                """));
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
                  until: 'inner.hasLegs'
                  maxIterations: 3
                gather: [findings]
                steps:
                  - name: inner
                    type: orient
              - name: unbouned
                repeat:
                  until: 'inner.hasLegs'
                steps:
                  - name: inner
                    type: orient
              - name: roundless
                repeat:
                  until: 'inner.hasLegs'
                  maxIterations: 3
                  blocking: 'any(check.problems, it != ''low'')'
                steps:
                  - name: inner
                    type: orient
              - name: blockless
                repeat:
                  until: 'inner.hasLegs'
                  rounds: 3
                steps:
                  - name: inner
                    type: orient
              - name: doubly
                repeat:
                  until: 'inner.hasLegs'
                  maxIterations: 3
                  rounds: 2
                  blocking: 'inner.hasLegs'
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
                "group 'doubly' repeats to maxIterations 3 and to rounds 2; one bound says the most iterations, the other says the rounds to follow and what keeps the loop going past them, and a loop with both would never tell which it was asked",
            ],
            problems);
    }

    /// <summary>
    /// A repeat's until is read once each iteration has ended, so it reads what the iterations produce: a member's
    /// output, by its name or its path from the root, or the previous iteration's. One that reads none of it - only steps
    /// before the loop, or the run's inputs - says the same after every iteration, and is refused.
    /// </summary>
    [Fact]
    public void ARepeatsUntil_ReadsWhatItsOwnStepsProduce()
    {
        var problems = Problems(
            """
            name: default
            inputs:
              - name: done
            steps:
              - name: check
                type: orient
              - name: blind
                repeat:
                  until: 'check.hasLegs || empty(input.done)'
                  maxIterations: 3
                steps:
                  - name: inner
                    type: orient
              - name: seeing
                repeat:
                  until: 'check.hasLegs && inner.hasLegs'
                  maxIterations: 3
                steps:
                  - name: inner
                    type: orient
              - name: by-path
                repeat:
                  until: 'by-path.deep.inner.hasLegs'
                  maxIterations: 3
                steps:
                  - name: deep
                    steps:
                      - name: inner
                        type: orient
              - name: remembering
                repeat:
                  until: 'previous.inner.hasLegs'
                  maxIterations: 3
                steps:
                  - name: inner
                    type: orient
            """);

        Assert.Equal(
            ["group 'blind' repeats until 'check.hasLegs || empty(input.done)', which reads nothing its own steps produce; no iteration could change what it says, so the loop would stop after its first or run to its bound"],
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
                from: 'orient.branch'
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
                "the workflow answers the kind 'unboundLoop', which is no question kind. Known: 'loopBound', 'visitBound', 'recoveryBound', 'openAgents', 'foldNewRows', 'foldLostRows', 'foldOverCells', 'foldTwoChanges', 'foldConflicts', 'foldOutside', 'turnLimit', 'truncated', 'stepBudget', 'contextLimit', 'runBudget', 'noChecks', 'checksUnsettled', 'checksPending', 'failure', 'pause', 'ask'.",
                "step 'asked' answers 'pause' and says person: the step is one only a person answers",
                "step 'asked' offers no options; a question with nothing to answer leaves a person typing into a run that reads nothing",
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

        // Nothing is planned again past a step of the flow's own stages, and the updated plan goes on beside its planner.
        Assert.Equal(
            [
                "amend.before names 'stage.inner', which is a step inside the group 'stage'; a change is planned again only while the flow's own stages still have a step to plan it",
                "amend.then names 'inner', which is no step declared beside 'replan'",
                "amend names neither input nor changedAnswerMinutes; at least one is named - the text input an amendment adds to, or the minutes the last answer can be changed for - and neither has a default",
                "amend.whenAddScope names 'replan', which is declared directly in no forEach group: a work item is built by a step of the group that runs once for each, the work-item group",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: replan
                    type: reason
                    agent: planner
                    prompt: prompts/update.md
                    outputs: schemas/update.json
                  - name: stage
                    steps:
                      - name: inner
                        type: orient
                amend:
                  before: stage.inner
                  step: replan
                  then: inner
                  scope: 'replan.scope'
                  whenAddScope: replan
                  whenChangeScope: replan
                  whenRemoveScope: replan
                """));

        Assert.Equal(
            [
                "amend.before names 'nowhere', which is no step of the flow",
                "amend.step names 'planning.replan', which is no step of the flow",
                "amend names neither input nor changedAnswerMinutes; at least one is named - the text input an amendment adds to, or the minutes the last answer can be changed for - and neither has a default",
                "amend.whenAddScope names 'replan', which is declared directly in no forEach group: a work item is built by a step of the group that runs once for each, the work-item group",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: replan
                    type: reason
                    agent: planner
                    prompt: prompts/update.md
                    outputs: schemas/update.json
                amend:
                  before: nowhere
                  step: planning.replan
                  scope: 'replan.scope'
                  whenAddScope: replan
                  whenChangeScope: replan
                  whenRemoveScope: replan
                """));
    }

    /// <summary>
    /// A plan update's scope is read once the update stands, when its planner and what its then set going have come to
    /// rest: every step is behind it, the group around its planner among them, which a step of the group could not
    /// read while it runs.
    /// </summary>
    [Fact]
    public void APlanUpdatesScope_ReadsTheGroupAroundItsPlanner()
    {
        Assert.Empty(Problems(
            """
            name: default
            inputs:
              - name: request
            steps:
              - name: planning
                gather: [scope]
                steps:
                  - name: replan
                    type: reason
                    agent: planner
                    prompt: prompts/update.md
                    outputs: schemas/update.json
                  - name: check
                    type: orient
                    dependsOn: [replan]
              - name: item-check
                type: validate-plan
                dependsOn: [planning]
                items: 'planning.replan.items'
              - name: items
                dependsOn: [item-check]
                forEach: 'item-check.items'
                steps:
                  - name: build
                    type: orient
            amend:
              input: request
              step: planning.replan
              then: check
              scope: 'planning.scope'
              whenAddScope: items.build
              whenChangeScope: items.build
              whenRemoveScope: items.build
            """));
    }

    /// <summary>
    /// A workflow that takes plan updates whole: a planner beside the plan it goes on with, a work-item group expanding
    /// from the items of the check it waits for through another step, and an alternative beside the step that builds an
    /// item. The tests of the amend block change one thing of it at a time.
    /// </summary>
    private const string Amended = """
        name: default
        inputs:
          - name: request
          - name: notes
            kind: file
        steps:
          - name: intake
            type: reason
            agent: triage
            prompt: prompts/intake.md
            outputs: schemas/intake.json
          - name: planning
            steps:
              - name: plan
                type: reason
                agent: planner
                prompt: prompts/plan.md
                outputs: schemas/plan.json
              - name: check
                type: validate-plan
                dependsOn: [plan]
                items: 'plan.latest.items'
              - name: plan-update
                type: reason
                resume: plan
                prompt: prompts/plan-update.md
                outputs: schemas/plan.json
          - name: item-check
            type: validate-plan
            dependsOn: [planning]
            items: 'planning.plan.latest.items'
          - name: gate
            type: orient
            dependsOn: [item-check]
          - name: items
            dependsOn: [gate]
            forEach: 'item-check.items'
            steps:
              - name: item-agent
                type: create-agent
                agent: implementer
              - name: implement
                type: reason
                agent: implementer
                access: write
                in: item-agent
                dependsOn: [item-agent]
                when: "piece.kind != 'removed'"
                prompt: prompts/implement.md
                outputs: schemas/implement.json
              - name: redo
                type: reason
                agent: implementer
                access: write
                in: item-agent
                prompt: prompts/redo.md
                outputs: schemas/implement.json
              - name: verify
                type: orient
                dependsOn: [implement]
        amend:
          input: request
          changedAnswerMinutes: 1
          step: planning.plan-update
          then: check
          scope: 'plan.latest.scope'
          whenAddScope: items.implement
          whenChangeScope: items.redo
          whenRemoveScope: items.implement
        """;

    /// <summary>
    /// V44: a plan update is taken for an amended request, for a changed answer, or both - so the amend block names at
    /// least one of input and changedAnswerMinutes, neither having a default - and an amendment adds words to a text input.
    /// </summary>
    [Fact]
    public void APlanUpdate_IsTakenForAnAmendmentOrAChangedAnswer_IntoATextInput()
    {
        Assert.Empty(Problems(Amended));
        Assert.Empty(Problems(Amended.Replace("  input: request\n", string.Empty, StringComparison.Ordinal)));
        Assert.Empty(Problems(Amended.Replace("  changedAnswerMinutes: 1\n", string.Empty, StringComparison.Ordinal)));

        Assert.Equal(
            ["amend names neither input nor changedAnswerMinutes; at least one is named - the text input an amendment adds to, or the minutes the last answer can be changed for - and neither has a default"],
            Problems(Amended.Replace("  input: request\n  changedAnswerMinutes: 1\n", string.Empty, StringComparison.Ordinal)));

        Assert.Equal(
            ["amend.input names 'notes', a file input; an amendment adds words to the request, which a text input holds"],
            Problems(Amended.Replace("input: request", "input: notes", StringComparison.Ordinal)));
    }

    /// <summary>
    /// V44: the step that plans an update is raised by an update alone, in the newest instance of its scope - so it is
    /// one step, in no forEach and no always group; it says no on, when, dependsOn or maxVisits; it goes on with the work
    /// of a step beside it; and its then never sends the run back to itself, which would be it calling itself.
    /// </summary>
    [Fact]
    public void TheStepThatPlansAnUpdate_IsOneStep_RaisedByAnUpdateAlone()
    {
        Assert.Equal(
            [
                "amend.step names 'planning.plan-update', which says when, dependsOn: the step that plans an update is raised by an update alone - never by an ending, never passed by, waiting for nothing and counted against no bound",
                "amend.step sends the run back to itself (amend.then: plan-update), and so calls itself; a step never does - one that is to run again after it failed says retries",
            ],
            Problems(Amended
                .Replace("resume: plan\n        prompt", "resume: plan\n        dependsOn: [check]\n        when: 'check.ok'\n        prompt", StringComparison.Ordinal)
                .Replace("then: check", "then: plan-update", StringComparison.Ordinal)));

        // One of many, going on with a step outside its own scope.
        Assert.Equal(
            [
                "amend.step names 'items.replan', which is inside the forEach 'items': the step that plans an update is one step, not one of many",
                "amend.step names 'items.replan', which goes on with 'intake', and 'intake' is not declared beside it: the step that plans an update goes on with the work of a step of its own scope",
            ],
            Problems(Amended
                .Replace(
                    "        dependsOn: [implement]\n",
                    "        dependsOn: [implement]\n      - name: replan\n        type: reason\n        resume: intake\n        prompt: prompts/plan-update.md\n        outputs: schemas/intake.json\n",
                    StringComparison.Ordinal)
                .Replace("  step: planning.plan-update\n  then: check\n  scope: 'plan.latest.scope'", "  step: items.replan\n  scope: 'replan.latest.scope'", StringComparison.Ordinal)));

        Assert.Equal(
            ["amend.step names 'wrap.replan', which is inside the always group 'wrap': the step that plans an update is raised while the run works, and an always group runs once, at the session's end"],
            Problems(Amended
                .Replace(
                    "amend:\n",
                    "  - name: wrap\n    always: true\n    steps:\n      - name: replan\n        type: reason\n        agent: planner\n        prompt: prompts/plan.md\n        outputs: schemas/plan.json\namend:\n",
                    StringComparison.Ordinal)
                .Replace("  step: planning.plan-update\n  then: check\n  scope: 'plan.latest.scope'", "  step: wrap.replan\n  scope: 'replan.latest.scope'", StringComparison.Ordinal)));
    }

    /// <summary>
    /// V46: whenAddScope names the step that builds a work item - declared directly in a forEach group, outside every
    /// always group, and no handler, since it begins its instance's work.
    /// </summary>
    [Fact]
    public void TheStepThatBuildsAWorkItem_BeginsAnInstanceOfAForEachGroup_WhileTheRunWorks()
    {
        Assert.Equal(
            [
                "amend.whenAddScope names 'items.retry', which is inside the always group 'items': a work item is built while the run works, and an always group runs once, at the session's end",
                "amend.whenAddScope names 'items.retry', a handler raised by 'implement': the step that builds a work item begins its instance's work, and a handler is raised by another step's ending",
            ],
            Problems(Amended
                .Replace("    dependsOn: [gate]\n", "    dependsOn: [gate]\n    always: true\n", StringComparison.Ordinal)
                .Replace(
                    "        dependsOn: [implement]\n",
                    "        dependsOn: [implement]\n      - name: retry\n        type: reason\n        agent: implementer\n        access: write\n        in: item-agent\n        on:\n          step: implement\n          outcome: failure\n        prompt: prompts/implement.md\n        outputs: schemas/implement.json\n",
                    StringComparison.Ordinal)
                .Replace("whenAddScope: items.implement", "whenAddScope: items.retry", StringComparison.Ordinal)
                .Replace("whenRemoveScope: items.implement", "whenRemoveScope: items.retry", StringComparison.Ordinal)));
    }

    /// <summary>
    /// V46: whenChangeScope and whenRemoveScope each name the step that builds a work item or an alternative beside it, of
    /// its type, that runs in its place - with its dependencies and under its name - so the alternative says no
    /// dependsOn, when, on, resume or always of its own, and no other step names it.
    /// </summary>
    [Fact]
    public void TheStepsThatCarryAnUpdate_AreAlternativesAtOnePlace_NamedByTheBuildersName()
    {
        Assert.Equal(
            [
                "amend.whenChangeScope names 'planning.plan', which is not declared beside 'items.implement': the three are alternatives at one place of one forEach group",
                "amend.whenRemoveScope names 'items.undo', a orient step, and 'items.implement' is a reason step: an alternative runs in that step's place, and is of its type",
                "amend.whenRemoveScope names 'items.undo', which says dependsOn, when: it runs in the place of 'implement', with that step's dependencies, and begins the work of its instance",
                "step 'items.after-undo' is raised by 'undo': 'undo' runs in the place of 'implement', and is named by that step's name",
                "step 'items.after-undo' sends the run to 'undo': 'undo' runs in the place of 'implement', and is named by that step's name",
                "step 'items.verify' depends on 'undo': 'undo' runs in the place of 'implement', and is named by that step's name",
            ],
            Problems(Amended
                .Replace(
                    "      - name: verify\n        type: orient\n        dependsOn: [implement]\n",
                    "      - name: undo\n        type: orient\n        dependsOn: [item-agent]\n        when: 'item.ok'\n      - name: after-undo\n        type: orient\n        on:\n          step: undo\n          outcome: failure\n        then: undo\n      - name: verify\n        type: orient\n        dependsOn: [implement, undo]\n",
                    StringComparison.Ordinal)
                .Replace("whenChangeScope: items.redo", "whenChangeScope: planning.plan", StringComparison.Ordinal)
                .Replace("whenRemoveScope: items.implement", "whenRemoveScope: items.undo", StringComparison.Ordinal)));

        // A step that goes on with an alternative's work names it too.
        Assert.Equal(
            ["step 'items.mend' resumes 'redo': 'redo' runs in the place of 'implement', and is named by that step's name"],
            Problems(Amended.Replace(
                "        dependsOn: [implement]\n",
                "        dependsOn: [implement]\n      - name: mend\n        type: reason\n        resume: redo\n        on:\n          step: verify\n          outcome: failure\n        prompt: prompts/mend.md\n        outputs: schemas/implement.json\n",
                StringComparison.Ordinal)));
    }

    /// <summary>
    /// V47: where a workflow takes plan updates, the work-item group expands from the items of a validate-plan step beside
    /// it that it waits for, directly or through others - the check the items an update places in a round are checked by
    /// with the round's own. A forEach refused for itself - yielding what is no list, reading a name nothing provides - is
    /// said once, for that. Without an amend block a forEach expands from whatever it reads.
    /// </summary>
    [Fact]
    public void TheWorkItemGroup_ExpandsFromTheItemsOfACheckItWaitsFor()
    {
        Assert.Equal(
            ["group 'items' expands from 'planning.plan.latest.items', which no validate-plan step checked: with an amend block the work items of a round are checked together with those an update places in it, by the check the group expands from"],
            Problems(Amended.Replace("forEach: 'item-check.items'", "forEach: 'planning.plan.latest.items'", StringComparison.Ordinal)));

        // One defect, said once: what is no list, and a name nothing provides, are refused for what they are.
        Assert.Equal(
            ["'items.forEach' yields a boolean, where a list is read: the group runs its steps once for each of its items"],
            Problems(Amended.Replace("forEach: 'item-check.items'", "forEach: 'true'", StringComparison.Ordinal)));

        Assert.Equal(
            ["'items.forEach' reads 'nothing.items', which no step before 'items', no input and no param provides"],
            Problems(Amended.Replace("forEach: 'item-check.items'", "forEach: 'nothing.items'", StringComparison.Ordinal)));

        Assert.Equal(
            ["group 'items' expands from 'item-check.items', and waits for 'item-check' neither directly nor through the steps it depends on: the work items it runs are the ones that check checked, once it has ended"],
            Problems(Amended.Replace("    dependsOn: [gate]\n    forEach", "    dependsOn: [planning]\n    forEach", StringComparison.Ordinal)));

        Assert.Empty(Problems(
            """
            name: default
            steps:
              - name: plan
                type: reason
                agent: planner
                prompt: prompts/plan.md
                outputs: schemas/plan.json
              - name: items
                dependsOn: [plan]
                forEach: 'plan.latest.items'
                steps:
                  - name: build
                    type: orient
            """));
    }

    /// <summary>
    /// V46: a piece is the part of a plan update one instance of the work-item group carries, so only a step of that group
    /// reads it - not a step outside it, and nothing in a workflow that has no such group.
    /// </summary>
    [Fact]
    public void APiece_IsReadOnlyByAStepOfTheWorkItemGroup()
    {
        Assert.Equal(
            ["'outside.when' reads 'piece.kind', whose 'piece' is the part of a plan update a work item carries, and 'outside' is no step of the work-item group 'items'"],
            Problems(Amended.Replace("amend:\n", "  - name: outside\n    type: orient\n    when: \"piece.kind == 'added'\"\namend:\n", StringComparison.Ordinal)));

        Assert.Equal(
            ["'alone.when' reads 'piece.kind', whose 'piece' is the part of a plan update a work item carries, and the workflow has no work-item group: amend.whenAddScope names the step of a forEach group that builds one"],
            Problems(
                """
                name: default
                steps:
                  - name: alone
                    type: orient
                    when: "piece.kind == 'added'"
                """));
    }

    /// <summary>
    /// V43, V44 and V45: what only a prompt reads - the repository's bar for a recommendation, the advisor's question, the
    /// reporter's status and a plan update - no step's own key reads, in a template or in an expression.
    /// </summary>
    [Fact]
    public void WhatOnlyAPromptReads_IsReadByNoStepsKey()
    {
        Assert.Equal(
            [
                "'orient.when' reads 'recommendations': the repository's bar for a recommendation is text for an agent, and only a reason step's prompt and the advisor's read it",
                "'orient.when' reads 'question.kind': only the advisor's prompt reads a question",
                "'save.message' reads 'status.question': only the reporter's prompts read the status",
                "'save.message' reads 'update.cause': a plan update is told to an agent, and only the prompts of the step that plans it and of the steps of its scope read one",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: orient
                    type: orient
                    when: "!empty(recommendations) && question.kind == 'loopBound'"
                  - name: save
                    type: commit
                    dependsOn: [orient]
                    message: '{status.question} {update.cause}'
                """));
    }

    /// <summary>
    /// <see cref="Amended"/> with an advisor, a reporter and a pull request whose body is a file: every prompt the engine
    /// asks an agent with, and the one template it fills from a file.
    /// </summary>
    private static readonly string AmendedAndAsked = Amended
        .Replace(
            "steps:\n  - name: intake",
            "advisor:\n  agent: advisor\n  prompt: prompts/recommend.md\n  tier: best\nreporter:\n  agent: reporter\n  statusPrompt: prompts/status.md\n  askPrompt: prompts/ask.md\nsteps:\n  - name: intake",
            StringComparison.Ordinal)
        .Replace("amend:\n", "  - name: pr\n    type: pull-request\n    dependsOn: [items]\n    title: 'done'\n    body: prompts/body.md\namend:\n", StringComparison.Ordinal);

    /// <summary>
    /// V43, V44, V45 and V46 in a prompt, each said at the line it first stands on: a reason step's prompt reads the bar,
    /// a plan update where it is the step that plans one or a step of its scope, and a piece where it is a step of the
    /// work-item group; the advisor's prompt reads a question and the bar, the reporter's two the status - the person's
    /// question in the askPrompt alone - and none of the three a step by name or a skill; a pull request's body, which
    /// the engine fills and no agent reads, reads what a step's own keys read, and no skill.
    /// </summary>
    [Fact]
    public void APrompt_ReadsWhatOnlyAPromptReads_WhereItsAgentIsAskedIt()
    {
        var definition = AmendedAndAsked;

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompts/intake.md"] = "Bar: {recommendations}\nUse {skill.notes}.\n{update.cause}\n{question.kind}\n{status.facts}\n{piece.kind}\n",
            ["prompts/plan.md"] = "Bar: {recommendations}\nUpdate: {update.cause}, {update.work}\n",
            ["prompts/plan-update.md"] = "Since: {update.since}\n",
            ["prompts/implement.md"] = "Piece: {piece.kind}\n",
            ["prompts/redo.md"] = "Before: {piece.before}\nWork: {update.work}\n",
            ["prompts/body.md"] = "Ran: {run.ran}\nBar: {recommendations}\nUse {skill.notes}.\n{update.cause}\n",
            ["prompts/recommend.md"] = "On {question.kind} at {question.step}: {question.record}\nFor {input.request}, by {recommendations}\n{status.facts}\n{planning.plan.summary}\n{skill.notes}\n",
            ["prompts/status.md"] = "Facts: {status.facts}\nAsked: {status.question}\n{recommendations}\n{intake.summary}\n",
            ["prompts/ask.md"] = "Asked: {status.question}, by {status.records}\n{question.kind}\n",
        };

        const string Bar = "the repository's bar for a recommendation is text for an agent, and only a reason step's prompt and the advisor's read it";
        const string Update = "a plan update is told to an agent, and only the prompts of the step that plans it and of the steps of its scope read one";
        const string Skill = "a skill is text for an agent, and only a reason step's prompt takes one";

        Assert.Equal(
            [
                $"step 'intake' prompt 'prompts/intake.md' line 3 reads 'update.cause': {Update}",
                "step 'intake' prompt 'prompts/intake.md' line 4 reads 'question.kind': only the advisor's prompt reads a question",
                "step 'intake' prompt 'prompts/intake.md' line 5 reads 'status.facts': only the reporter's prompts read the status",
                "step 'intake' prompt 'prompts/intake.md' line 6 reads 'piece.kind', whose 'piece' is the part of a plan update a work item carries, and 'intake' is no step of the work-item group 'items'",
                $"step 'items.redo' prompt 'prompts/redo.md' line 2 reads 'update.work': {Update}",
                $"step 'pr' body 'prompts/body.md' line 2 reads 'recommendations': {Bar}",
                $"step 'pr' body 'prompts/body.md' line 3 names {{skill.notes}}: {Skill}",
                $"step 'pr' body 'prompts/body.md' line 4 reads 'update.cause': {Update}",
                "advisor prompt 'prompts/recommend.md' line 3 reads 'status.facts': only the reporter's prompts read the status",
                "advisor prompt 'prompts/recommend.md' line 4 reads 'planning.plan.summary': the advisor's prompt reads no step by name; what the run stored of the steps a question is about is {question.record}",
                $"advisor prompt 'prompts/recommend.md' line 5 names {{skill.notes}}: {Skill}",
                "reporter statusPrompt 'prompts/status.md' line 2 reads 'status.question': the status is the tool's own question, and no person wrote one",
                $"reporter statusPrompt 'prompts/status.md' line 3 reads 'recommendations': {Bar}",
                "reporter statusPrompt 'prompts/status.md' line 4 reads 'intake.summary': the reporter's prompts read no step by name; the run's directory, {status.records}, is all the reporter reads",
                "reporter askPrompt 'prompts/ask.md' line 2 reads 'question.kind': only the advisor's prompt reads a question",
            ],
            Problems(definition, files));

        Assert.Empty(Problems(definition));
    }

    /// <summary>
    /// V44: the scope of a step that plans an update at the top level is the flow, every step of which reads the update
    /// in its prompts - but the recovery, whose steps are no step of it.
    /// </summary>
    [Fact]
    public void APlanUpdate_PlannedAtTheTopLevel_IsReadByEveryPromptOfTheFlow_ButTheRecoverys()
    {
        var definition = Amended
            .Replace("      - name: plan-update\n        type: reason\n        resume: plan\n        prompt: prompts/plan-update.md\n        outputs: schemas/plan.json\n", string.Empty, StringComparison.Ordinal)
            .Replace("  - name: item-check\n", "  - name: plan-update\n    type: reason\n    resume: intake\n    prompt: prompts/plan-update.md\n    outputs: schemas/intake.json\n  - name: item-check\n", StringComparison.Ordinal)
            .Replace("  step: planning.plan-update\n  then: check\n  scope: 'plan.latest.scope'", "  step: plan-update\n  then: item-check\n  scope: 'planning.plan.latest.scope'", StringComparison.Ordinal)
            .Replace("  - name: gate\n    type: orient\n", "  - name: gate\n    type: orient\n    onFailure: recover\n", StringComparison.Ordinal)
            + "\nrecover:\n  onSuccess: failed-step\n  onFailure: pause\n  steps:\n    - name: mend\n      type: reason\n      agent: fixer\n      prompt: prompts/mend.md\n      outputs: schemas/mend.json\n";

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompts/intake.md"] = "Update: {update.cause}\n",
            ["prompts/redo.md"] = "Work: {update.work}\n",
            ["prompts/mend.md"] = "Update: {update.cause}\n",
        };

        Assert.Equal(
            ["step 'recover.mend' prompt 'prompts/mend.md' line 1 reads 'update.cause': a plan update is told to an agent, and only the prompts of the step that plans it and of the steps of its scope read one"],
            Problems(definition, files));
    }

    /// <summary>V44: a workflow with no amend block takes no plan update, so no prompt of it reads one.</summary>
    [Fact]
    public void APlanUpdate_IsReadByNoPrompt_OfAWorkflowThatTakesNone()
    {
        Assert.Equal(
            ["step 'replan' prompt 'prompts/replan.md' line 1 reads 'update.cause': a plan update is told to an agent, and the workflow takes none: it declares no amend block"],
            Problems(
                """
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                  - name: replan
                    type: reason
                    dependsOn: [plan]
                    resume: plan
                    prompt: prompts/replan.md
                    outputs: schemas/plan.json
                """,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["prompts/plan.md"] = "Plan.\n", ["prompts/replan.md"] = "Again, for {update.cause}.\n" }));
    }

    /// <summary>
    /// What only some prompts read is typed by what the engine gives under it: a question, the status, a plan update and
    /// a piece each give the texts part 04 names and nothing else, and the bar is one text, as each run fact is - a run
    /// fact being text a template reads and no value for an expression.
    /// </summary>
    [Fact]
    public void WhatOnlySomePromptsRead_IsTypedByTheTextsTheEngineGives()
    {
        var definition = AmendedAndAsked.Replace(
            "  - name: verify\n        type: orient\n        dependsOn: [implement]\n",
            "  - name: verify\n        type: orient\n        dependsOn: [implement]\n        when: \"os == 'linux' && piece.kind == 'added'\"\n",
            StringComparison.Ordinal);

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompts/intake.md"] = "Bar: {recommendations}\n{recommendations.extra}\nIn {worktree}, {worktree.path}\n",
            ["prompts/plan.md"] = "Update: {update.cause}, {update.since}, {update.work}\n{update.reason}\n{update}\n",
            ["prompts/implement.md"] = "Piece: {piece.kind} {piece.changed} {piece.keep} {piece.undo} {piece.instead} {piece.cause} {piece.before}\n{piece.kinds}\n{piece.before.x}\n",
            ["prompts/recommend.md"] = "On {question.step} {question.kind} {question.problem} {question.answers} {question.record}\n{question.answer}\n",
            ["prompts/status.md"] = "Facts: {status.facts}, {status.records}\n{status.fact}\n",
            ["prompts/ask.md"] = "Asked: {status.question}\n",
        };

        const string Update = "a plan update is update.cause, update.since, update.work, and nothing else";

        Assert.Equal(
            [
                "step 'intake' prompt 'prompts/intake.md' line 2 reads 'recommendations.extra', and the bar for a recommendation says 'recommendations' is a text, which has no property 'extra'",
                "step 'intake' prompt 'prompts/intake.md' line 3 reads 'worktree.path', and the run says 'worktree' is a text, which has no property 'path'",
                $"step 'planning.plan' prompt 'prompts/plan.md' line 2 reads 'update.reason', and {Update}",
                $"step 'planning.plan' prompt 'prompts/plan.md' line 3 reads 'update', and {Update}",
                "step 'items.implement' prompt 'prompts/implement.md' line 2 reads 'piece.kinds', and a piece is piece.kind, piece.changed, piece.keep, piece.undo, piece.instead, piece.cause, piece.before, and nothing else",
                "step 'items.implement' prompt 'prompts/implement.md' line 3 reads 'piece.before.x', and a piece says 'piece.before' is a text, which has no property 'x'",
                "'items.verify.when' reads 'os', which is a run fact, text a template reads, not a value for an expression: an expression reads the run by run.<name>",
                "advisor prompt 'prompts/recommend.md' line 2 reads 'question.answer', and a question is question.step, question.kind, question.problem, question.answers, question.record, and nothing else",
                "reporter statusPrompt 'prompts/status.md' line 2 reads 'status.fact', and the status is status.facts, status.question, status.records, and nothing else",
            ],
            Problems(definition, files));
    }

    /// <summary>
    /// V43: the advisor recommends on the questions the engine raises itself - never on an agent's own, which carries its
    /// own recommendation, nor on the run's budget at its reserve, when nothing more is spent on reasoning.
    /// </summary>
    [Fact]
    public void TheAdvisor_RecommendsNeitherOnAnAgentsQuestionNorOnTheRunsBudget()
    {
        const string Advised = """
            name: default
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
              kinds: [ask, loopBound, runBudget]
            steps:
              - name: orient
                type: orient
            """;

        Assert.Equal(
            [
                "advisor is to recommend for 'ask': an agent that asks gives its own recommendation",
                "advisor is to recommend for 'runBudget': a run at its budget's reserve spends nothing more on reasoning, and the advisor reasons",
            ],
            Problems(Advised));
        Assert.Empty(Problems(Advised.Replace("[ask, loopBound, runBudget]", "[loopBound, failure]", StringComparison.Ordinal)));
    }

    /// <summary>
    /// V31: pushCheckpoints pushes the checkpoint commits as they are made, so a workflow that sets it with no push step
    /// that is a checkpoint says something nothing would do; one with such a step, or that leaves it false, holds.
    /// </summary>
    [Fact]
    public void PushCheckpoints_WithNoPushStepThatIsACheckpoint_IsRefused()
    {
        const string Flow = """
            name: default
            pushCheckpoints: true
            steps:
              - name: save
                type: commit
                message: 'work'
              - name: send
                type: push
                dependsOn: [save]
            """;

        Assert.Equal(["the workflow sets pushCheckpoints, and none of its push steps is a checkpoint: the key would change nothing"], Problems(Flow));
        Assert.Empty(Problems(Flow + "\n    checkpoint: true"));
        Assert.Empty(Problems(Flow.Replace("pushCheckpoints: true", "pushCheckpoints: false", StringComparison.Ordinal)));
    }

    /// <summary>
    /// V36: two handlers of one source that one ending could raise never both name a then - the run can go to one step.
    /// Every ending is weighed: a success, a failure with any code, the codes two failures share, an end, which is either,
    /// and an end with codes, which is a failure with one of them alone. A handler that names no then sends nothing.
    /// </summary>
    [Fact]
    public void TwoHandlersOneEndingRaises_NeverBothNameAThen()
    {
        Assert.Equal(
            [
                "steps 'fix' and 'report' are both raised when 'build' fails with 20 or 21, and both name a then: the run can go to one step",
                "steps 'report' and 'note' are both raised when 'build' fails with 30, and both name a then: the run can go to one step",
                "steps 'report' and 'cheer' are both raised when 'build' succeeds, and both name a then: the run can go to one step",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: build
                    type: build
                  - name: fix
                    type: orient
                    on: { step: build, outcome: failure, codes: [20, 21] }
                    then: build
                  - name: report
                    type: orient
                    on: { step: build, outcome: end }
                    then: build
                  - name: note
                    type: orient
                    on: { step: build, outcome: failure, codes: [30] }
                    then: build
                  - name: cheer
                    type: orient
                    on: { step: build, outcome: success }
                    then: build
                  - name: quiet
                    type: orient
                    on: { step: build, outcome: failure }
                """));

        Assert.Equal(
            ["steps 'first' and 'second' are both raised when 'build' fails, and both name a then: the run can go to one step"],
            Problems(
                """
                name: default
                steps:
                  - name: build
                    type: build
                  - name: first
                    type: orient
                    on: { step: build, outcome: failure }
                    then: build
                  - name: second
                    type: orient
                    on: { step: build, outcome: failure }
                    then: build
                  - name: coded-end
                    type: orient
                    on: { step: build, outcome: end, codes: [5] }
                  - name: cheer
                    type: orient
                    on: { step: build, outcome: success }
                    then: build
                """));
    }

    /// <summary>
    /// The agent keys a step, the advisor and the reporter took before the configuration owned them: each is refused
    /// naming what replaced it, so a reader moves the key's meaning instead of deleting its line.
    /// </summary>
    [Fact]
    public void RetiredAgentKeys_AreRefusedNamingWhatReplacedThem()
    {
        Assert.Equal(
            [
                "line 5, column 3: 'fallback' is not a key of advisor; a fallback is the order of the tier's models in tierConfig.",
                "line 6, column 3: 'provider' is not a key of advisor; a model's code is listed by one provider, which it names.",
                "line 11, column 3: 'fallback' is not a key of reporter; a fallback is the order of the tier's models in tierConfig.",
                "line 12, column 3: 'provider' is not a key of reporter; a model's code is listed by one provider, which it names.",
                "line 17, column 5: 'fallback' is not a key of a step; a fallback is the order of the tier's models in tierConfig.",
                "line 18, column 5: 'provider' is not a key of a step; a model's code is listed by one provider, which it names.",
            ],
            Refusals(
                """
                name: default
                advisor:
                  agent: advisor
                  prompt: prompts/recommend.md
                  fallback: [m1]
                  provider: someone
                reporter:
                  agent: reporter
                  statusPrompt: prompts/status.md
                  askPrompt: prompts/ask.md
                  fallback: [m2]
                  provider: someone
                steps:
                  - name: think
                    type: reason
                    agent: thinker
                    fallback: [m5]
                    provider: someone
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """));
    }

    /// <summary>
    /// V38's config half, for each agent the definition runs: what a step names is resolved against the
    /// configuration's providers and tiers, so a model no provider lists, a tier nothing defines, an effort a model
    /// does not take, a client that does not reach the provider, and a step that names neither where the config names
    /// neither either, are all refused with the step that names them. Every model of the tier is checked, the ones
    /// this machine cannot use included; and where the config declares no providers at all, the sentence says what to
    /// paste.
    /// </summary>
    [Fact]
    public void V38sConfigHalf_RefusesWhatTheConfigurationDoesNotResolve_NamingTheStep()
    {
        var providers = Providers();

        // A model no provider lists, said with the lists.
        Assert.Equal(
            ["workflow 'default' step 'plan' resolves to model 'claude-opus-6', which no provider lists. Listed: zai: glm-5.3. Add it to its provider's models under providers"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    model: claude-opus-6
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, providers));

        // A tier nothing defines.
        Assert.Equal(
            ["workflow 'default' step 'plan' names tier 'deep', which tierConfig does not define. Defined: balanced"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    tier: deep
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, providers));

        // An effort a model of the tier does not take - every model of the tier checked, glm-5.3 among them - with
        // the level to give it instead: the nearest it takes at or above the one asked.
        Assert.Equal(
            ["workflow 'default' step 'plan' runs at effort 'medium', which 'glm-5.3' (tier 'balanced') does not take. It takes: low, high, max. Give it a level of its own under effort: glm-5.3: high"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    tier: balanced
                    effort: medium
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, providers));

        // A step that names neither a tier nor a model, where the configuration names no default tier.
        Assert.Equal(
            ["workflow 'default' step 'plan' names neither a tier nor a model, and the configuration names no defaults.agentTier; say one on the step, or under defaults.agentTier"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, Providers(agentTier: null)));

        // Where the config declares no providers at all, the sentence says what to paste - whatever the step names: a
        // tier, a model, or neither, the default tier then naming a tier no configuration defines.
        var none = AgentProviders.Resolve(new HarnessConfig { Defaults = new HarnessDefaults { AgentTier = "balanced" } }).Providers!;

        Assert.Equal(
            [
                "workflow 'default' step 'plan' names tier 'balanced' and the config declares no providers and no tierConfig: dssharness list-agent-providers --template prints them as init writes them into a new config, to paste",
                "workflow 'default' step 'draft' names model 'glm-5.3' and the config declares no providers and no tierConfig: dssharness list-agent-providers --template prints them as init writes them into a new config, to paste",
                "workflow 'default' step 'write' names neither a tier nor a model and the config declares no providers and no tierConfig: dssharness list-agent-providers --template prints them as init writes them into a new config, to paste",
            ],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    tier: balanced
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                  - name: draft
                    type: reason
                    agent: thinker
                    model: glm-5.3
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                  - name: write
                    type: reason
                    agent: thinker
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, none));

        // With no providers given at all, the half is unchecked: a definition is never checked against a configuration
        // the caller did not choose.
        Assert.Empty(WorkflowValidator.Validate(Parse("""
            name: default
            steps:
              - name: plan
                type: reason
                agent: thinker
                tier: deep
                prompt: prompts/think.md
                outputs: schemas/think.json
            """), hasCommitTemplate: false, files: null, providers: null));
    }

    /// <summary>
    /// An effort given for each model is checked model by model: a level a model does not take is refused naming the
    /// level to give it instead where the level came from all, a model named by no entry and no all runs at its own
    /// default, and a step that goes on with another's work names either form against the tier it inherits.
    /// </summary>
    [Fact]
    public void AnEffortForEachModel_IsCheckedModelByModel()
    {
        var providers = TwoModels();

        // The map's all gives every model it does not name: glm-5.3 does not take xhigh, and the refusal names the
        // nearest level it takes at or above the one asked.
        Assert.Equal(
            ["workflow 'default' step 'plan' runs 'glm-5.3' (tier 'best') at effort 'xhigh', which it does not take. It takes: low, high, max. Give it a level of its own under effort: glm-5.3: max"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    tier: best
                    effort:
                      all: xhigh
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, providers));

        // Where the model's own entry gave the level, the first sentence stands alone: it has a level of its own, and
        // the level is what is wrong.
        Assert.Equal(
            ["workflow 'default' step 'plan' runs 'glm-5.3' (tier 'best') at effort 'medium', which it does not take. It takes: low, high, max"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    tier: best
                    effort:
                      all: high
                      glm-5.3: medium
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, providers));

        // A model named by no entry and no all runs at its own default, and nothing is refused of it.
        Assert.Empty(WorkflowValidator.Validate(Parse("""
            name: default
            steps:
              - name: plan
                type: reason
                agent: thinker
                tier: best
                effort:
                  glm-5.3: max
                prompt: prompts/think.md
                outputs: schemas/think.json
            """), hasCommitTemplate: false, files: null, providers));

        // A step that goes on with another's work names either form, checked against the tier it inherits.
        Assert.Equal(
            ["workflow 'default' step 'mend' runs 'glm-5.3' (tier 'best') at effort 'xhigh', which it does not take. It takes: low, high, max. Give it a level of its own under effort: glm-5.3: max"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: implement
                    type: reason
                    agent: thinker
                    tier: best
                    effort: high
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                  - name: mend
                    type: reason
                    resume: implement
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                    effort:
                      all: xhigh
                """), hasCommitTemplate: false, files: null, providers));

        // A step that names neither a tier nor a model runs on the configuration's default tier, its models checked
        // like a named tier's own - here balanced, GLM's alone.
        Assert.Equal(
            ["workflow 'default' step 'plan' runs at effort 'medium', which 'glm-5.3' (tier 'balanced') does not take. It takes: low, high, max. Give it a level of its own under effort: glm-5.3: high"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    effort: medium
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, providers));
    }

    /// <summary>A code under a step's effort that is no model it runs on is refused rather than ignored: a repository that changes its tier then hears which steps still name the old model.</summary>
    [Fact]
    public void AnEffortForAModelTheStepDoesNotRunOn_IsRefused()
    {
        Assert.Equal(
            ["workflow 'default' step 'plan' gives an effort to 'glm-5.4', which it does not run on. It runs on: claude-fable-5-1, glm-5.3"],
            WorkflowValidator.Validate(Parse("""
                name: default
                steps:
                  - name: plan
                    type: reason
                    agent: thinker
                    tier: best
                    effort:
                      all: high
                      glm-5.4: max
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                """), hasCommitTemplate: false, files: null, TwoModels()));
    }

    /// <summary>A test step's filterWhen is an expression as when is, with the same reads, and must yield a boolean.</summary>
    [Fact]
    public void AFilterWhenThatIsNoBoolean_IsRefused()
    {
        Assert.Equal(
            ["'verify.filterWhen' yields a text, where a boolean is read: nothing is read as true or as false but true and false"],
            WorkflowValidator.Validate(Parse("""
                name: default
                inputs:
                  - name: request
                steps:
                  - name: verify
                    type: test
                    filterWhen: input.request
                """), hasCommitTemplate: false, files: null, providers: null));
    }

    /// <summary>One provider, one model, one tier, as the config-half tests spell the configuration.</summary>
    private static AgentProviders? Providers(string? agentTier = "balanced")
    {
        var config = new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = agentTier },
            Providers =
            [
                new()
                {
                    Name = "zai", Type = "zai", Api = "https://api.z.ai", ApiKeyNames = ["ZAI_API_KEY"], Client = "glm",
                    Currency = "USD", ReadOn = "2026-10-12",
                    Models =
                    [
                        new() { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["balanced"], Efforts = ["low", "high", "max"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 },
                    ],
                },
            ],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
        };

        return AgentProviders.Resolve(config).Providers;
    }

    /// <summary>
    /// Two companies' models on two tiers, as the effort-by-model tests spell the configuration: Claude's and GLM's on
    /// best, GLM's alone on balanced - the configuration's default, so a step that inherits a tier is told from one it
    /// would otherwise run on.
    /// </summary>
    private static AgentProviders? TwoModels()
    {
        var config = new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = "balanced" },
            Providers =
            [
                new()
                {
                    Name = "anthropic", Type = "anthropic", Api = "https://api.anthropic.com", ApiKeyNames = ["ANTHROPIC_API_KEY"], Client = "claude-code",
                    Currency = "USD", ReadOn = "2026-10-12",
                    Models =
                    [
                        new() { Code = "claude-fable-5-1", Name = "Claude Fable 5.1", Tiers = ["best"], Efforts = ["low", "medium", "high", "xhigh", "max"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 },
                    ],
                },
                new()
                {
                    Name = "zai", Type = "zai", Api = "https://api.z.ai", ApiKeyNames = ["ZAI_API_KEY"], Client = "glm",
                    Currency = "USD", ReadOn = "2026-10-12",
                    Models =
                    [
                        new() { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["best", "balanced"], Efforts = ["low", "high", "max"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 },
                    ],
                },
            ],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["best"] = ["claude-fable-5-1", "glm-5.3"],
                ["balanced"] = ["glm-5.3"],
            },
        };

        return AgentProviders.Resolve(config).Providers;
    }

    /// <summary>Reads a definition's parse refusals, whole with their place in the file.</summary>
    private static IReadOnlyList<string> Refusals(string text)
    {
        try
        {
            _ = WorkflowFileParser.Parse("workflows/implementation/implementation.yml", text);
        }
        catch (HarnessException ex)
        {
            return [.. ((string)ex.Message).Split(" - ").Select(line => line.Trim()).Skip(1)];
        }

        return [];
    }

    /// <summary>
    /// V39: what a block gathers is read after its name as its members are, so each name is gathered once, and none is
    /// one of the engine's facts of a step or the name of a member, which that path would read in its place.
    /// </summary>
    [Fact]
    public void ABlocksGatheredNames_AreEachOnce_AndNeitherAFactNorAMember()
    {
        Assert.Equal(
            [
                "group 'review' gathers 'findings' twice; a block gathers one list of each name",
                "group 'review' gathers 'code', which after a step's name is one of the engine's own facts of it (outcome, code, error, question, latest, assumptions): 'review.code' would read that fact and the gathered list at once. Gather it under another name",
                "group 'review' gathers 'correctness', the name of one of its members: 'review.correctness' would read that member and the gathered list at once. Gather it under another name",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: review
                    gather: [findings, code, findings, correctness]
                    steps:
                      - name: correctness
                        type: orient
                      - name: tests
                        type: orient
                """));
    }

    /// <summary>
    /// V40: a pause step's answer is one of its options, given once each time the step asks - never counted, so never
    /// given times or a then; an answer that is just the option holds.
    /// </summary>
    [Fact]
    public void APauseStepsAnswer_IsGivenOnce_NeverCountedNorFollowed()
    {
        const string Paused = """
            name: default
            steps:
              - name: ask
                type: pause
                question: go on?
                options: [continue, again]
                answers:
                  pause:
                    answer: again
                    times: 2
                    then: continue
            """;

        Assert.Equal(
            [
                "step 'ask' answers 'pause' with again 2 times; times count an answer after which the same question can come again - more, retry or compact - and a pause step's answer ends the matter",
                "step 'ask' answers 'pause' then continue; then is what is answered once the counted answers are spent, and a pause step's answer is never counted",
            ],
            Problems(Paused));
        Assert.Empty(Problems(Paused.Replace("\n        times: 2\n        then: continue", string.Empty, StringComparison.Ordinal)));
    }

    /// <summary>
    /// V30, where the configuration declares no commit template: the step's message is the commit's, and variables, which
    /// fill a template's placeholders, would be read by nothing. Under a template the validation service holds them to the
    /// variables it declares.
    /// </summary>
    [Fact]
    public void ACommitStepsVariables_WithNoTemplate_AreRefused()
    {
        const string Variables = """
            name: default
            steps:
              - name: save
                type: commit
                message: 'work'
                variables:
                  type: feat
            """;

        Assert.Equal(
            ["step 'save' gives variables and the configuration declares no commit.template; variables fill a template's placeholders, and with none its message is the commit's and they would be read by nothing"],
            Problems(Variables));
        Assert.Empty(WorkflowValidator.Validate(Parse(Variables), hasCommitTemplate: true));
    }

    /// <summary>
    /// Where the configuration declares no commit template, a commit step writes its message, which is required; where it
    /// declares one, the template writes it and a step's own message is not used - never refused, so that one definition,
    /// the shipped one among them, holds in a repository with a template and in one without.
    /// </summary>
    [Fact]
    public void ACommitStep_WritesTheMessageTheConfigurationLeavesIt()
    {
        var bare =
            """
            name: default
            steps:
              - name: commit
                type: commit
            """;

        Assert.Equal(["step 'commit' names no message and the configuration declares no commit.template; one of the two writes the commit's message"], Problems(bare));
        Assert.Empty(WorkflowValidator.Validate(Parse(bare), hasCommitTemplate: true));

        var written = bare + "\n    message: 'work done'";

        Assert.Empty(Problems(written));
        Assert.Empty(WorkflowValidator.Validate(Parse(written), hasCommitTemplate: true));
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
                "step 'mender' is raised by the codes 0, 20, and an exit code is from 1 to 255",
                "step 'mender': maxVisits must be from 1 to 20, found 0",
                "step 'mender' sends the run to 'elsewhere', which is no step it sees; a redirect goes to a step beside it or beside a group around it, never into a group from outside",
                "step 'sends' says then without on; then is where a handler sends the run when it is done, and a step the schedule starts has nothing to send back",
            ],
            problems);
    }

    /// <summary>
    /// An expression or a template that says nothing, or is not written whole - a quote, a placeholder or a parenthesis
    /// left open - is refused with where it went wrong: a read past such a place would never be checked, and an empty
    /// one is a definition that says nothing where it was meant to say something. Never a defect of the tool's own.
    /// </summary>
    [Fact]
    public void AnEmptyOrUnclosedExpression_IsRefusedWithWhereItWentWrong()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
              - name: blank
                type: orient
                when: ''
              - name: quoted
                type: orient
                when: "orient.answer == 'approve && plann.ok"
              - name: opened
                type: orient
                when: '(orient.ok && !orient.red'
              - name: closed
                type: orient
                when: 'orient.ok) && orient.red'
              - name: check
                type: validate-plan
                items: ' '
              - name: commit
                type: commit
                message: |-
                  {orient.title

                  {plann.title}
            """);

        Assert.Equal(
            [
                "'blank.when' is empty; an expression is read for what it says, and an empty one says nothing",
                "'quoted.when' is not written whole: the quote at column 18 is never closed, and nothing it reads past there would be checked",
                "'opened.when' is not written whole: 1 parenthesis is opened and never closed, and nothing it reads past there would be checked",
                "'closed.when' is not written whole: the parenthesis at column 10 closes nothing that was opened, and nothing it reads past there would be checked",
                "'check.items' is empty; an expression is read for what it says, and an empty one says nothing",
                "'commit.message' is not written whole: the placeholder at column 1 of line 1 is never closed, and nothing it reads past there would be checked",
            ],
            problems);
    }

    /// <summary>
    /// The recovery sees every step of the flow, wherever it is declared, and reads the step that failed as source;
    /// nothing of the flow reads the recovery's steps, and source is read only by a handler or by the recovery. A step
    /// reaches it by saying onFailure: recover, which needs a recovery declared and is never said inside it, and a
    /// recovery no step gives a failure to is said to be one. Its
    /// visits are bounded as a handler's are, and a plan update is never planned or placed by one of its steps.
    /// </summary>
    [Fact]
    public void TheRecovery_SeesEveryStepOfTheFlow_AndNothingOfTheFlowSeesIt()
    {
        var problems = Problems(
            """
            name: default
            inputs:
              - name: request
            steps:
              - name: orient
                type: orient
              - name: reads-recovery
                type: orient
                when: 'recover.diagnose.ok'
              - name: reads-source
                type: orient
                when: "source.code == 20"
              - name: last
                type: orient
            amend:
              input: request
              step: recover.diagnose
              scope: 'diagnose.scope'
              whenAddScope: recover.diagnose
              whenChangeScope: recover.diagnose
              whenRemoveScope: recover.diagnose
            recover:
              onSuccess: failed-step
              onFailure: pause
              maxVisits: 0
              steps:
                - name: diagnose
                  type: reason
                  agent: diagnostician
                  prompt: prompts/diagnose.md
                  outputs: schemas/diagnosis.json
                  when: "last.hasLegs && source.code != 13"
                - name: fixes
                  dependsOn: [diagnose]
                  forEach: 'diagnose.items'
                  steps:
                    - name: make
                      type: create-agent
                      agent: fixer
                    - name: verify
                      type: test
                      in: make
                      when: "orient.hasLegs && item.ok"
                      onFailure: recover
            """);

        Assert.Equal(
            [
                "recover.maxVisits must be from 1 to 99, found 0",
                "the workflow declares recover, and no step gives it a failure or an issue to fix; a step says onFailure: recover, itself or through a group around it, for its failures to reach it, or onAddress: recover for the pre-existing issues it decides to fix",
                "'reads-recovery.when' reads 'recover.diagnose.ok': the recovery's steps are read by their own names inside it, and nothing outside it reads them",
                "'reads-source.when' reads 'source.code', whose 'source' names the step whose ending raised a handler, and 'reads-source' is neither a handler, nor in one, nor a step of the recovery, nor one raised by a pre-existing issue",
                "step 'recover.fixes.verify' is a step of the recovery and gives its failure to the recovery, and so calls itself; a step never does - one that is to run again after it failed says retries",
                "amend.step names 'recover.diagnose', a step of the recovery; a plan update is planned and placed by the flow's own steps",
                "amend.whenAddScope names 'recover.diagnose', a step of the recovery; a plan update is planned and placed by the flow's own steps",
                "amend.whenChangeScope names 'recover.diagnose', a step of the recovery; a plan update is planned and placed by the flow's own steps",
                "amend.whenRemoveScope names 'recover.diagnose', a step of the recovery; a plan update is planned and placed by the flow's own steps",
            ],
            problems);

        // A reasoning step is never recovered, by its own onFailure or by a group's around it.
        Assert.Equal(
            [
                "step 'think' is a reasoning step and gives its failure to the recovery; a reasoning step is never recovered - its agent is tried again and on its fallback, and what still fails is asked: say pause",
                "step 'stage.plan' is a reasoning step, and its failure would go to the recovery by the onFailure of 'stage'; a reasoning step is never recovered - say pause on it",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: build
                    type: build
                    onFailure: recover
                  - name: think
                    type: reason
                    agent: thinker
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                    onFailure: recover
                  - name: stage
                    onFailure: recover
                    steps:
                      - name: plan
                        type: reason
                        agent: planner
                        prompt: prompts/plan.md
                        outputs: schemas/plan.json
                recover:
                  onSuccess: failed-step
                  onFailure: pause
                  steps:
                    - name: diagnose
                      type: orient
                """));

        // The recovery is held to the same rule as every step: it never sends a failure of its own back to itself, and
        // never passes one over.
        foreach (var (policy, refusal) in new[]
        {
            ("recover", "recover gives a failure of its own work to the recovery (onFailure: recover), and so calls itself; a step never does - one that is to run again after it failed says retries"),
            ("continue", "recover passes a failure of its own work over (onFailure: continue); the step that gave it its failure would be neither put right nor failed - say pause or stop"),
        })
        {
            Assert.Equal(
                [refusal],
                Problems(
                    $"""
                    name: default
                    steps:
                      - name: build
                        type: build
                        onFailure: recover
                    recover:
                      onSuccess: failed-step
                      onFailure: {policy}
                      steps:
                        - name: diagnose
                          type: orient
                    """));
        }

        // A step of the recovery that says no onFailure does what the recovery's own says, as a member does its group's: a
        // reasoning step there would be recovered where the recovery gives its failures to itself.
        Assert.Equal(
            [
                "recover gives a failure of its own work to the recovery (onFailure: recover), and so calls itself; a step never does - one that is to run again after it failed says retries",
                "step 'recover.diagnose' is a reasoning step, and its failure would go to the recovery by the onFailure of 'recover'; a reasoning step is never recovered - say pause on it",
            ],
            Problems(
                """
                name: default
                steps:
                  - name: build
                    type: build
                    onFailure: recover
                recover:
                  onSuccess: failed-step
                  onFailure: recover
                  steps:
                    - name: diagnose
                      type: reason
                      agent: diagnostician
                      prompt: prompts/diagnose.md
                      outputs: schemas/diagnosis.json
                """));

        // A step gives its failure to a recovery only where the workflow declares one.
        Assert.Equal(
            ["step 'test' gives its failure to the recovery (onFailure: recover), and the workflow declares no recover; declare one, or say what the failure does"],
            Problems(
                """
                name: default
                steps:
                  - name: test
                    type: test
                    onFailure: recover
                """));
    }

    /// <summary>
    /// An answer given beforehand is one its kind offers, counted only where the same question can come again and
    /// ended only by an answer that ends the matter; a person's kind, a failure's and an ask's are never answered by
    /// a configuration, a budget only by the workflow, a pause only on its own step, and skip only on a skippable one.
    /// </summary>
    [Fact]
    public void AnAnswer_IsOneItsKindOffers_AndAPersonsKindIsNeverAnswered()
    {
        var problems = Problems(
            """
            name: default
            answers:
              foldLostRows: allow
              loopBound: bogus
              turnLimit:
                answer: retry
                times: 50
                then: retry
            steps:
              - name: loop
                repeat:
                  until: 'think.ok'
                  maxIterations: 2
                steps:
                  - name: think
                    type: reason
                    agent: thinker
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                    answers:
                      failure: retry
                      ask: decide
                      runBudget: more
                      pause: go
                      contextLimit: skip
              - name: draft
                type: reason
                agent: thinker
                prompt: prompts/think.md
                outputs: schemas/think.json
                skippable: true
                answers:
                  truncated:
                    answer: retry
                    times: 2
                    then: skip
              - name: checks
                type: ci-wait
                dependsOn: [loop]
                answers:
                  checksPending:
                    answer: accept
                    times: 2
                  noChecks: ''
              - name: mender
                type: orient
                on:
                  step: checks
                  outcome: failure
                answers:
                  visitBound:
                    answer: more
                    then: accept
              - name: asked
                type: pause
                dependsOn: [checks]
                question: now?
                options: [go]
                answers:
                  pause: stop
            """);

        Assert.Equal(
            [
                "the workflow answers 'foldLostRows': a lost or an over cell at a fold is a person's to accept, and no line of a configuration accepts it",
                "the workflow answers 'loopBound' with 'bogus', which it does not offer. Its answers: 'more', 'accept', 'abort'.",
                "the workflow: the times of the answer to 'turnLimit' must be from 1 to 20, found 50",
                "the workflow answers 'turnLimit' then 'retry', which is not one of its answers that end the matter: 'skip', 'fail', 'abort'",
                "step 'loop.think' answers 'failure': what a step's failure does is its onFailure, which answers that question beforehand",
                "step 'loop.think' answers 'ask': whether a step may put its questions to a person is the step's own ask",
                "step 'loop.think' answers 'runBudget': the run's budget is the workflow's, and only the workflow's own answers answer for it",
                "step 'loop.think' answers 'pause': a pause step's question is answered on that step alone",
                "step 'loop.think' answers 'contextLimit' with skip, and 'loop.think' is no skippable step; skip ends a step skipped only where it says skippable",
                "step 'checks' answers 'checksPending' with accept 2 times; times count an answer after which the same question can come again - more, retry or compact - and accept ends the matter",
                "step 'checks' answers the kind 'noChecks' with nothing; an answer is spelled as the question's options spell it",
                "step 'mender' answers 'visitBound' with more and no times: say how many times, from 1 to 20",
                "step 'mender' answers 'visitBound' then accept with no times; then is what is answered once the counted answers are spent",
                "step 'asked' answers 'pause' with 'stop', which is not one of the options the step offers",
            ],
            problems);
    }

    /// <summary>
    /// An answer given beforehand is to a question its step can raise - on a group, one some step in it raises, and on
    /// the workflow one some step of it raises - since one to a question nothing raises would be read by nothing; and an
    /// answer after which the same question can come again says how many times it is given.
    /// </summary>
    [Fact]
    public void AnAnswer_IsToAKindItsStepRaises_AndOneThatGoesOnIsCounted()
    {
        var problems = Problems(
            """
            name: default
            answers:
              noChecks:
                answer: retry
                times: 2
              turnLimit: retry
            steps:
              - name: make
                type: create-agent
                agent: implementer
              - name: think
                type: reason
                agent: thinker
                prompt: prompts/think.md
                outputs: schemas/think.json
                answers:
                  loopBound: accept
                  contextLimit: compact
              - name: stage
                onFailure: recover
                answers:
                  checksUnsettled: accept
                  visitBound:
                    answer: more
                    times: 2
                  recoveryBound: abort
                steps:
                  - name: build
                    type: build
                  - name: mend
                    type: orient
                    on:
                      step: build
                      outcome: failure
                  - name: drop
                    type: delete-agent
                    of: make
                    discardUncommitted: true
                    answers:
                      foldNewRows: allow
                  - name: release
                    type: delete-agent
                    of: make
                    answers:
                      foldNewRows: allow
                      foldConflicts:
                        answer: retry
                        times: 1
                  - name: catch-up
                    type: refresh
                    of: make
                    answers:
                      foldTwoChanges: merge
                      foldOutside: fail
                  - name: redirect
                    type: orient
                    on:
                      step: catch-up
                      outcome: failure
                    then: build
                    answers:
                      openAgents: stay
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: orient
                  answers:
                    recoveryBound: abort
            """);

        Assert.Equal(
            [
                "the workflow answers 'noChecks', which no step of it raises; the answer would be read by nothing",
                "the workflow answers 'turnLimit' with retry and no times: say how many times, from 1 to 20",
                "step 'think' answers 'loopBound', which it never raises; the answer would be read by nothing",
                "step 'think' answers 'contextLimit' with compact and no times: say how many times, from 1 to 20",
                "group 'stage' answers 'checksUnsettled', which no step in it raises; the answer would be read by nothing",
                "step 'stage.drop' answers 'foldNewRows', which it never raises; the answer would be read by nothing",
                "step 'stage.catch-up' answers 'foldOutside', which it never raises; the answer would be read by nothing",
                "step 'recover.diagnose' answers 'recoveryBound', which it never raises; the answer would be read by nothing",
            ],
            problems);
    }

    /// <summary>
    /// A leg step's in names the create-agent step whose worktree it works in, as a reason step's does; an anchor
    /// check's rows and paths, given as expressions, read what every expression may.
    /// </summary>
    [Fact]
    public void ALegStepsIn_AndAnAnchorChecksReads_AreResolvedAsEveryNameIs()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
              - name: built
                type: build
                in: nowhere
              - name: tested
                type: test
                in: built
              - name: balance
                type: anchor-balance
                rows: 'fold.rows'
              - name: citations
                type: anchor-citations
                paths: 'fold.written'
            """);

        Assert.Equal(
            [
                "step 'built' works in 'nowhere', which is no step an enclosing group declares",
                "step 'tested' works in 'built', which is a build step; a worktree is made by a create-agent step",
                "'balance.rows' reads 'fold.rows', which no step before 'balance', no input and no param provides",
                "'citations.paths' reads 'fold.written', which no step before 'citations', no input and no param provides",
            ],
            problems);
    }

    /// <summary>
    /// A handler's keys are whole: it is raised by another step, by codes from 1 to 255 given once each and never beside
    /// a success, waits for nothing but its source, is never always, and is visited from 1 to 20 times; only a handler
    /// says then or maxVisits, and its then goes to a step it sees - outward too - never to itself. A group is held to
    /// the same bounds as a typed step.
    /// </summary>
    [Fact]
    public void AHandlersKeys_AreWhole_AndAGroupIsHeldToTheSameBounds()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: gate
                type: test
              - name: stage
                retries: -1
                maxVisits: 2
                steps:
                  - name: build
                    type: build
                  - name: self
                    type: orient
                    on:
                      step: self
                      outcome: failure
                  - name: codes
                    type: orient
                    on:
                      step: build
                      outcome: failure
                      codes: [300, 20, 20]
                    maxVisits: 21
                  - name: succeeded
                    type: orient
                    dependsOn: [build]
                    always: true
                    on:
                      step: build
                      outcome: success
                      codes: [1]
                    then: succeeded
                  - name: outward
                    type: orient
                    on:
                      step: build
                      outcome: failure
                    then: gate
            """);

        Assert.Equal(
            [
                "group 'stage': retries must be from 1 to 20, found -1",
                "group 'stage' says maxVisits without on; it bounds how often a handler is raised, and the schedule starts a step once",
                "step 'stage.self' is raised by its own ending, and so calls itself; a step never does - one that is to run again after it failed says retries",
                "step 'stage.codes' is raised by the codes 300, 20, 20, and an exit code is from 1 to 255",
                "step 'stage.codes' is raised by the code 20 more than once",
                "step 'stage.codes': maxVisits must be from 1 to 20, found 21",
                "step 'stage.succeeded' is raised by a success with codes; a step that ended done ended with 0, and codes are a failure's",
                "step 'stage.succeeded' is raised by 'build' and also depends on 'build': a handler waits for its source's ending and for nothing else",
                "step 'stage.succeeded' is raised by 'build' and is always; an always step is started by the end of the session, never by a step",
                "step 'stage.succeeded' sends the run back to itself, and so calls itself; a step never does - one that is to run again after it failed says retries",
            ],
            problems);
    }

    /// <summary>
    /// The workflow's own bounds and its loops' are the ranges the help gives; a loop past its rounds with no bound but
    /// the budget needs a budget; a tier and a model are never named together; asking is bounded; a plan update may be
    /// given no iteration at all.
    /// </summary>
    [Fact]
    public void TheBoundsOfAWorkflow_AreTheRangesItsKeysTake()
    {
        var problems = Problems(
            """
            name: default
            maxReasoningAgents: 40
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              retries: 0
              backoffSeconds: 1000
            maxRetries: 21
            backoffSeconds: 901
            maxInstances: 7
            handoffSeconds: 5
            budget:
              usd: 1
              reservePercent: 60
            inputs:
              - name: request
            steps:
              - name: orient
                type: orient
              - name: loop
                repeat:
                  until: 'think.ok'
                  maxIterations: 30
                steps:
                  - name: think
                    type: reason
                    agent: thinker
                    tier: best
                    model: some-model
                    prompt: prompts/think.md
                    outputs: schemas/think.json
                    ask: true
                    askRounds: 6
                    backoffSeconds: 0
            amend:
              input: request
              step: loop.think
              scope: 'think.scope'
              whenAddScope: loop.think
              whenChangeScope: loop.think
              whenRemoveScope: loop.think
              maxAddedIterations: 0
            """);

        Assert.Equal(
            [
                "maxReasoningAgents must be from 1 to 32, found 40",
                "maxRetries must be from 1 to 20, found 21",
                "backoffSeconds must be from 1 to 900, found 901",
                "maxInstances must be from 100 to 100,000, found 7",
                "handoffSeconds must be from 30 to 900, found 5",
                "budget.reservePercent must be from 1 to 50, found 60",
                "advisor.retries must be from 1 to 20, found 0",
                "advisor.backoffSeconds must be from 1 to 900, found 1000",
                "group 'loop': repeat.maxIterations must be from 1 to 20, found 30",
                "step 'loop.think': askRounds must be from 1 to 5, found 6",
                "step 'loop.think': backoffSeconds must be from 1 to 900, found 0",
                "step 'loop.think' names a model and a tier: the model would be used and the tier never read",
                "amend.whenAddScope names 'loop.think', which is declared directly in no forEach group: a work item is built by a step of the group that runs once for each, the work-item group",
            ],
            problems);

        // The counts of what runs at once, the reporter's tries, every wait of git, the forge and the checks, a loop's
        // rounds, and how long an answer can be changed for.
        Assert.Equal(
            [
                "maxParallelProcesses is 0, and so not one or more",
                "maxOpenAgents is 0, and so not one or more",
                "reporter.retries must be from 1 to 20, found 21",
                "reporter.backoffSeconds must be from 1 to 900, found 0",
                "step 'ci': limit is 0, and so not one or more",
                "step 'ci': timeoutMinutes must be from 1 to 240, found 241",
                "step 'checks': appearSeconds must be from 30 to 3,600, found 29",
                "step 'checks': waitMinutes must be from 1 to 720, found 721",
                "step 'checks': pollSeconds must be from 10 to 600, found 601",
                "step 'checks': timeoutMinutes must be from 1 to 240, found 0",
                "step 'commit': timeoutMinutes must be from 1 to 240, found 0",
                "step 'push': timeoutMinutes must be from 1 to 240, found 241",
                "step 'pr': timeoutMinutes must be from 1 to 240, found 0",
                "group 'rounds': repeat.rounds must be from 1 to 20, found 21",
                "group 'rounds': repeat.blockersLimitRounds must be from 1 to 20, found 0",
                "amend.changedAnswerMinutes must be from 1 to 60, found 61",
                "amend.maxAddedIterations must be from 0 to 10, found 11",
                "amend.whenAddScope names 'replan', which is declared directly in no forEach group: a work item is built by a step of the group that runs once for each, the work-item group",
            ],
            Problems(
                """
                name: default
                maxParallelProcesses: 0
                maxOpenAgents: 0
                reporter:
                  agent: reporter
                  statusPrompt: prompts/status.md
                  askPrompt: prompts/ask.md
                  retries: 21
                  backoffSeconds: 0
                steps:
                  - name: replan
                    type: reason
                    agent: planner
                    prompt: prompts/plan.md
                    outputs: schemas/plan.json
                  - name: ci
                    type: ci-read
                    limit: 0
                    timeoutMinutes: 241
                  - name: checks
                    type: ci-wait
                    appearSeconds: 29
                    waitMinutes: 721
                    pollSeconds: 601
                    timeoutMinutes: 0
                  - name: commit
                    type: commit
                    message: 'work'
                    timeoutMinutes: 0
                  - name: push
                    type: push
                    timeoutMinutes: 241
                  - name: pr
                    type: pull-request
                    title: 'pr'
                    body: prompts/body.md
                    timeoutMinutes: 0
                  - name: rounds
                    repeat:
                      until: 'inner.hasLegs'
                      rounds: 21
                      blocking: 'inner.hasLegs'
                      blockersLimitRounds: 0
                    steps:
                      - name: inner
                        type: orient
                amend:
                  changedAnswerMinutes: 61
                  maxAddedIterations: 11
                  step: replan
                  scope: 'replan.scope'
                  whenAddScope: replan
                  whenChangeScope: replan
                  whenRemoveScope: replan
                """));

        var unbounded =
            """
            name: default
            steps:
              - name: loop
                repeat:
                  until: 'review.hasLegs'
                  rounds: 3
                  blocking: 'review.hasLegs'
                  blockersLimitRounds: false
                steps:
                  - name: review
                    type: orient
            """;

        Assert.Equal(["group 'loop': repeat.blockersLimitRounds is false and the workflow has no budget: nothing would bound the loop"], Problems(unbounded));
        Assert.Empty(Problems(unbounded.Replace("steps:\n  - name: loop", "budget:\n  usd: 5\nsteps:\n  - name: loop", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Only a reason step's work has a latest output, or assumptions: of any other step, by its name or its path from
    /// the root, either is refused.
    /// </summary>
    [Fact]
    public void OnlyAReasonStepsWork_HasALatestOutputOrAssumptions()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: group
                steps:
                  - name: a
                    type: orient
                  - name: b
                    type: orient
                    when: 'a.ok && !a.latest.blocked'
              - name: think
                type: reason
                agent: thinker
                prompt: prompts/think.md
                outputs: schemas/think.json
              - name: c
                type: orient
                when: '!empty(think.latest.blocked) && empty(think.assumptions) && empty(group.a.assumptions)'
            """);

        Assert.Equal(
            [
                "'group.b.when' reads 'a.latest.blocked', and 'group.a' is a orient step: only a reason step's work has a latest output",
                "'c.when' reads 'group.a.assumptions', and 'group.a' is a orient step: only a reason step's work has assumptions",
            ],
            problems);
    }

    /// <summary>
    /// A name is looked up from the inside out, so an inner step never takes a name it can see from outside by bare name
    /// - a step beside a group around it, declared before or after, or that group itself; and the recovery's steps,
    /// which see the flow's top level by bare name, take none of its names. Steps of the same name in two groups neither
    /// of which holds the other see nothing of each other, and are each read by their own group's path.
    /// </summary>
    [Fact]
    public void AnInnerStep_NeverTakesANameItCanSeeFromOutside()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: orient
                type: orient
                onFailure: recover
              - name: outer
                steps:
                  - name: orient
                    type: orient
                  - name: outer
                    type: orient
                  - name: later
                    type: orient
              - name: later
                type: orient
              - name: kept
                steps:
                  - name: balance
                    type: orient
              - name: other
                steps:
                  - name: balance
                    type: orient
                  - name: checks
                    steps:
                      - name: kept
                        type: orient
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: orient
                  type: orient
                - name: balance
                  type: orient
            """);

        Assert.Equal(
            [
                "step 'outer.orient' has the name of step 'orient', which it can see from where it is: names are looked up from the inside out, so the inner one would be read in its place",
                "step 'outer.outer' has the name of step 'outer', which it can see from where it is: names are looked up from the inside out, so the inner one would be read in its place",
                "step 'outer.later' has the name of step 'later', which it can see from where it is: names are looked up from the inside out, so the inner one would be read in its place",
                "step 'other.checks.kept' has the name of step 'kept', which it can see from where it is: names are looked up from the inside out, so the inner one would be read in its place",
                "step 'recover.orient' has the name of step 'orient', which it can see from where it is: names are looked up from the inside out, so the inner one would be read in its place",
            ],
            problems);
    }

    /// <summary>
    /// No step is named for a word a definition reads for its own - a root a read starts with, a fact of the engine's
    /// read after a step's name, or a literal - and the list is the one the names and scopes of the plan give, with
    /// param, which the code reads as a root. A read starting with a run fact reads the run; one starting with a fact of
    /// the engine's names no step, and is refused.
    /// </summary>
    [Fact]
    public void AStep_IsNamedNoWordADefinitionReadsForItsOwn()
    {
        string[] reserved =
        [
            "run", "carried", "previous", "source", "item", "it", "index", "iteration", "visit", "input", "recover", "skill",
            "recommendations", "update", "piece", "status", "true", "false", "null", "empty", "any",
            "outcome", "code", "error", "question", "latest", "assumptions",
            "worktree", "work", "plans", "rows", "orchestrator", "agent", "step", "os",
            "param", "issue",
        ];

        Assert.Equal(reserved.Order(StringComparer.Ordinal), WorkflowExpressions.ReservedNames.Order(StringComparer.Ordinal));
        Assert.All(WorkflowExpressions.ReservedRoots, root => Assert.Contains(root, WorkflowExpressions.ReservedNames));

        var problems = Problems(
            """
            name: default
            steps:
              - name: work
                type: orient
              - name: 'true'
                type: orient
              - name: param
                type: orient
              - name: stage
                steps:
                  - name: code
                    type: orient
              - name: commit
                type: commit
                message: '{os} {worktree} {latest.items}'
            """);

        Assert.Equal(
            [
                "step 'work' is named 'work', a word a definition reads for its own; a read could not tell the step from the word",
                "step 'true' is named 'true', a word a definition reads for its own; a read could not tell the step from the word",
                "step 'param' is named 'param', a word a definition reads for its own; a read could not tell the step from the word",
                "step 'stage.code' is named 'code', a word a definition reads for its own; a read could not tell the step from the word",
                "'commit.message' reads 'latest.items', which no step before 'commit', no input and no param provides",
            ],
            problems);
    }

    /// <summary>
    /// A refresh or a rebase works on one agent (of) or on every live one but the agent others names, never both and
    /// never neither, and each names a create-agent step; a deletion that discards folds nothing, so it says nothing of
    /// what a fold may carry; a step that only reads writes nothing.
    /// </summary>
    [Fact]
    public void TheFoldFamily_NamesTheAgentsItWorksOn_AndADiscardFoldsNothing()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: make
                type: create-agent
                agent: implementer
              - name: both
                type: refresh
                of: make
                others: make
              - name: neither
                type: rebase
              - name: wrong
                type: refresh
                others: neither
              - name: dropped
                type: delete-agent
                of: make
                discardUncommitted: true
                within: [src/**]
              - name: reads
                type: reason
                agent: reader
                prompt: prompts/read.md
                outputs: schemas/read.json
                writes: [src/**]
              - name: lost
                type: fold
                of: nowhere
              - name: weighs-both
                type: refresh
                of: make
                paths: [src/a.c]
                all: true
            """);

        Assert.Equal(
            [
                "step 'both' names 'of' and 'others'; it works on one agent, or on every other one, and never both",
                "step 'neither' names neither 'of' nor 'others'; it works on one agent, or on every live one but the agent 'others' names",
                "step 'wrong' names 'neither' as its others, which is a rebase step; an agent is made by a create-agent step",
                "step 'dropped' discards what is not committed and says what its fold may carry; a deletion that discards folds nothing, so allow and within would be read by nothing",
                "step 'reads' names writes and only reads; a step that writes says access: write and names the create-agent step it works in",
                "step 'lost' names 'nowhere' as its of, which is no step an enclosing group declares",
                "step 'weighs-both' takes refresh: all weighs every path the main tree moved, so it takes no path beside it: give the paths, or all",
            ],
            problems);
    }

    /// <summary>
    /// An always step runs at the session's end: no handler is raised by its ending, and no redirect goes to it. A
    /// handler is raised by its own sibling's ending, sends the run nowhere from inside an always group - which runs once
    /// and is never gone back over - and never to a step that waits for the group the handler is in, which would start no
    /// sooner.
    /// </summary>
    [Fact]
    public void AnAlwaysStep_RaisesNoHandler_AndIsNeverRedirectedTo()
    {
        var problems = Problems(
            """
            name: default
            steps:
              - name: gate
                type: test
              - name: report
                type: orient
                always: true
              - name: on-report
                type: orient
                on:
                  step: report
                  outcome: end
              - name: stage
                steps:
                  - name: build
                    type: build
                  - name: stranger
                    type: orient
                    on:
                      step: gate
                      outcome: failure
                  - name: to-report
                    type: orient
                    on:
                      step: build
                      outcome: failure
                    then: report
                  - name: to-waiter
                    type: orient
                    on:
                      step: build
                      outcome: failure
                      codes: [2]
                    then: waiter
              - name: waiter
                type: orient
                dependsOn: [stage]
              - name: closing
                always: true
                steps:
                  - name: check
                    type: test
                  - name: mend
                    type: orient
                    on:
                      step: check
                      outcome: failure
                    then: check
            """);

        Assert.Equal(
            [
                "step 'on-report' is raised by 'report', an always step; an always step runs at the session's end, when nothing is left to handle its ending",
                "step 'stage.stranger' is raised by 'gate', which is no step declared beside it; a handler is raised by its own siblings",
                "step 'stage.to-report' sends the run to 'report', an always step, which runs once at the session's end and is never gone back to",
                "step 'stage.to-waiter' sends the run to 'waiter', which depends on 'stage', the group the handler is in: it waits for that group, and would start no sooner",
                "step 'closing.mend' sends the run to 'check' from inside the always group 'closing'; an always group runs once, at the session's end, and is never gone back over",
                "steps 'stage.to-report' and 'stage.to-waiter' are both raised when 'build' fails with 2, and both name a then: the run can go to one step",
            ],
            problems);
    }

    /// <summary>
    /// A pause offers each answer once - and at least one, whether its options are left out or given empty; a pre-answer
    /// gives one of them, with a text exactly where that option needs one, reads an input or quotes words from one, and a
    /// quote is looked for in a declared text input, the words a person gave.
    /// </summary>
    [Fact]
    public void APreAnswer_IsWhatThePersonGaveWithTheRun_WithATextExactlyWhereItsOptionNeedsOne()
    {
        var problems = Problems(
            """
            name: default
            inputs:
              - name: request
              - name: plan
                kind: file
            steps:
              - name: approval
                type: pause
                question: approve?
                options: [approve, revise, approve]
                text: [revise]
                preAnswer:
                  - when: "input.request == 'go'"
                    answer: revise
                  - when: "input.request == 'go'"
                    answer: approve
                    text: 'as it is'
                  - when: 'true'
                    answer: approve
                  - when: "input.request != ''"
                    answer: maybe
                  - when: 'true'
                    answer: approve
                    quote:
                      text: "'approved'"
                      input: plan
                  - when: 'true'
                    answer: approve
                    quote:
                      text: "'approved'"
                      input: missing
                  - when: "input.request == 'change'"
                    answer: revise
                    text: 'smaller'
              - name: empty-offer
                type: pause
                question: now?
                options: []
            """);

        Assert.Equal(
            [
                "step 'approval' offers one option more than once",
                "step 'approval' is answered beforehand with 'revise' and no text; 'revise' needs a text with it, and the answer would carry none",
                "step 'approval' is answered beforehand with 'approve' and a text; 'approve' takes no text, and this one would be read by nothing",
                "step 'approval' is answered beforehand by an entry that reads no input and quotes none: a pre-answer is what the person gave with the run, and an answer the configuration gives whatever was typed is written under answers",
                "step 'approval' is answered beforehand with 'maybe', which is not one of the options it offers",
                "step 'approval' quotes the input 'plan', a file input; a quote is looked for in the words a person gave, which a text input holds",
                "step 'approval' quotes the input 'missing', which the workflow does not declare",
                "step 'empty-offer' offers no options; a question with nothing to answer leaves a person typing into a run that reads nothing",
            ],
            problems);
    }

    /// <summary>
    /// What the workflow declares is named as everything reads it, and each declaration holds: an input lists each of its
    /// options once, a param a default supplies is never also required, an output is named as an input is, and the
    /// advisor is asked only about kinds there are.
    /// </summary>
    [Fact]
    public void TheWorkflowsDeclarations_AreNamedAsReadAndEachHolds()
    {
        var problems = Problems(
            """
            name: default
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              kinds: [loopBound, unboundLoop]
            inputs:
              - name: request
                options: [yes, 'no', yes]
              - name: step
              - name: Upper
            params:
              - name: rounds
                default: '3'
                required: true
              - name: os
            outputs:
              - name: Title
                from: 'orient.branch'
            steps:
              - name: orient
                type: orient
            """);

        Assert.Equal(
            [
                "input 'request' lists 'yes' among its options more than once; an option is one value a run may be started with, and a second says nothing the first did not",
                "the input 'step' is named with a word a definition reads for its own; name it another way",
                "the input 'Upper' is not a word with a lower-case letter first: letters, digits and dashes within",
                "param 'rounds' is required and declares a default: a default always supplies the value, so required would never refuse. Give the default, or require the value.",
                "the param 'os' is named with a word a definition reads for its own; name it another way",
                "advisor.kinds names 'unboundLoop', which is no question kind. Known: 'loopBound', 'visitBound', 'recoveryBound', 'openAgents', 'foldNewRows', 'foldLostRows', 'foldOverCells', 'foldTwoChanges', 'foldConflicts', 'foldOutside', 'turnLimit', 'truncated', 'stepBudget', 'contextLimit', 'runBudget', 'noChecks', 'checksUnsettled', 'checksPending', 'failure', 'pause', 'ask'.",
                "the output 'Title' is not a word with a lower-case letter first: letters, digits and dashes within",
            ],
            problems);
    }

    /// <summary>
    /// A budget counts at least one amount, and every amount it gives is above zero - a zero beside another amount would
    /// pause the run before it began as surely as a zero alone.
    /// </summary>
    [Fact]
    public void ABudget_CountsSomething_AndEveryAmountItGivesIsAboveZero()
    {
        Assert.Equal(
            [
                "budget.usd must be above zero, found 0",
                "budget.minutes must be above zero, found -5",
            ],
            Problems(
                """
                name: default
                budget:
                  usd: 0
                  tokens: 1000
                  minutes: -5
                steps:
                  - name: orient
                    type: orient
                """));

        Assert.Equal(
            ["budget.tokens must be above zero, found -1"],
            Problems(
                """
                name: default
                budget:
                  tokens: -1
                steps:
                  - name: orient
                    type: orient
                """));

        Assert.Equal(
            ["budget holds no usd, tokens or minutes; a budget with nothing to count is no budget. Give at least one, or take the section out to have none."],
            Problems(
                """
                name: default
                budget:
                  reservePercent: 20
                steps:
                  - name: orient
                    type: orient
                """));
    }

    /// <summary>
    /// A read of the run's own names says which one it reads - an input, a param, a member of the previous iteration
    /// that its loop holds - 'it' is read only inside an any(...) test, a skill only in a prompt, and a template hands on
    /// something. What a placeholder says after '|' is how its value is written, and is never read as a name.
    /// </summary>
    [Fact]
    public void AReadOfTheRunsOwnNames_SaysWhichOneItReads()
    {
        var problems = Problems(
            """
            name: default
            inputs:
              - name: request
            params:
              - name: rounds
            steps:
              - name: loop
                repeat:
                  until: 'inner.hasLegs'
                  maxIterations: 2
                steps:
                  - name: inner
                    type: orient
                  - name: reads
                    type: orient
                    when: 'empty(input) || empty(param) || previous.ghost.ok || it.severity || skill.notes'
              - name: said
                type: pull-request
                dependsOn: [loop]
                title: ''
                body: prompts/body.md
              - name: commit
                type: commit
                dependsOn: [said]
                message: '{plann.title | upper} {loop.inner.branch|lower}'
            """);

        Assert.Equal(
            [
                "'loop.reads.when' reads 'input', which names no input; read 'input.<name>'",
                "'loop.reads.when' reads 'param', which names no param; read 'param.<name>'",
                "'loop.reads.when' reads 'previous.ghost.ok', and 'ghost' is no step of the group 'loop' repeats",
                "'loop.reads.when' reads 'it.severity', whose 'it' names the member an any(...) test reads, and this read is in none",
                "'loop.reads.when' names a skill, and a skill's text is a prompt's alone; the step's own keys read the run's names, never a skill",
                "'said.title' is empty; a template is the text a step hands on, and an empty one hands on nothing",
                "'commit.message' reads 'plann.title', which no step before 'commit', no input and no param provides",
            ],
            problems);

        // Parentheses left open are counted, as one is.
        Assert.Equal(
            ["'opened.when' is not written whole: 2 parentheses are opened and never closed, and nothing it reads past there would be checked"],
            Problems(
                """
                name: default
                steps:
                  - name: orient
                    type: orient
                  - name: opened
                    type: orient
                    when: '((orient.ok'
                """));
    }

    /// <summary>
    /// V15: the first cycle the walk closes is said whole - each node a step's path - and a handler waits for its
    /// source: a source that depends on its own handler, as a step may, would wait for an ending that waits for it.
    /// The recovery's own steps are walked too.
    /// </summary>
    [Fact]
    public void ADependencyCycle_IsSaidWhole()
    {
        Assert.Equal(
            ["the workflow has a dependency cycle: fix -> build -> fix"],
            Problems("""
                name: flow
                steps:
                  - name: fix
                    type: orient
                    on:
                      step: build
                      outcome: failure
                  - name: build
                    type: build
                    dependsOn: [fix]
                """));

        Assert.Equal(
            ["the workflow has a dependency cycle: recover.fix -> recover.build -> recover.fix"],
            Problems("""
                name: flow
                steps:
                  - name: plan
                    type: orient
                    onFailure: recover
                recover:
                  onSuccess: failed-step
                  onFailure: pause
                  steps:
                    - name: fix
                      type: orient
                      on:
                        step: build
                        outcome: failure
                    - name: build
                      type: build
                      dependsOn: [fix]
                """));
    }

    /// <summary>
    /// A handler's source is a sibling and nothing else: one named from inside a group that waits for it is refused as
    /// no sibling, and draws no cycle - the group waits for the step, and the handler would wait for it too.
    /// </summary>
    [Fact]
    public void AHandlersSourceOutsideItsGroup_IsNoSibling_AndNoCycle()
    {
        Assert.Equal(
            ["step 'gate.h' is raised by 's', which is no step declared beside it; a handler is raised by its own siblings"],
            Problems("""
                name: flow
                steps:
                  - name: s
                    type: orient
                  - name: gate
                    dependsOn: [s]
                    steps:
                      - name: h
                        type: orient
                        on:
                          step: s
                          outcome: failure
                """));
    }

    /// <summary>
    /// Every name a read starts with is looked up from where the reader is (<c>FlowScope</c>): a sibling block's member
    /// by the block's bare name from inside a group, a recovery step's sibling from inside the recovery, a repeat's own
    /// members from within its loop, and a member nested deeper in it, in the iteration before, by its path from the
    /// repeat's members. Nothing else is: a member of a group from outside it by bare name, a member nested deeper than
    /// a loop's own - now, or in the iteration before by bare name, which the refusal says the path of - a group's
    /// member in the group's when, by bare name or by path, read before any member ran, the iteration before in a
    /// repeat group's own when, read before its first, the group around the reader, which has not ended while the
    /// reader runs, and a forEach's member from outside it, which would be one of many.
    /// </summary>
    [Fact]
    public void EveryRead_IsLookedUpFromWhereTheReaderIs()
    {
        Assert.Empty(Problems("""
            name: flow
            steps:
              - name: plan
                type: orient
                onFailure: recover
              - name: outer
                steps:
                  - name: review
                    steps:
                      - name: correctness
                        type: orient
                  - name: after
                    type: orient
                    dependsOn: [review]
                    when: review.correctness.hasLegs
              - name: rounds
                repeat:
                  until: 'audit.hasLegs && outer.review.correctness.hasLegs && previous.check.soundness.hasLegs'
                  maxIterations: 3
                steps:
                  - name: audit
                    type: orient
                  - name: check
                    steps:
                      - name: soundness
                        type: orient
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: orient
                - name: remedy
                  type: orient
                  dependsOn: [diagnose]
                  when: 'diagnose.hasLegs && plan.hasLegs'
            """));

        Assert.Equal(
            [
                "'gate.when' reads 'inner.ok', which no step before 'gate', no input and no param provides",
                "'gate.when' reads 'gate.inner.ok', which no step before 'gate', no input and no param provides",
                "'gate.inner.when' reads 'gate.outcome', and 'gate' is not upstream of it: 'gate' holds 'gate.inner', and has not ended while it runs",
                "'rounds.when' reads 'previous.audit.ok', whose 'previous' names the iteration of a repeat group before this one, and no group holding 'rounds' runs one",
                "'rounds.repeat.until' reads 'correctness.ok', which no step before 'rounds', no input and no param provides",
                "'rounds.repeat.until' reads 'previous.correctness.ok', and 'correctness' is a step of the group 'rounds' repeats, nested deeper than a bare name reaches from here: read 'previous.review.correctness'",
                "'after.when' reads 'items.verify.ok', and 'items' runs a forEach: read from outside it, a member is one of many, and nothing says which",
                "'after.when' reads 'inner.ok', which no step before 'after', no input and no param provides",
            ],
            Problems("""
                name: flow
                steps:
                  - name: plan
                    type: orient
                  - name: items
                    forEach: 'plan.dirtyPaths'
                    steps:
                      - name: verify
                        type: orient
                  - name: gate
                    when: 'inner.ok || gate.inner.ok'
                    steps:
                      - name: inner
                        type: orient
                        when: 'gate.outcome'
                  - name: rounds
                    when: 'previous.audit.ok'
                    repeat:
                      until: 'correctness.ok || audit.ok || previous.correctness.ok'
                      maxIterations: 3
                    steps:
                      - name: audit
                        type: orient
                      - name: review
                        steps:
                          - name: correctness
                            type: orient
                  - name: after
                    type: orient
                    when: 'items.verify.ok || inner.ok'
                """));
    }

    /// <summary>
    /// A step's name is a lower-case word with dashes within, and nothing after it: a name that ends in a line's end -
    /// which a pattern anchored with <c>$</c> would take - is refused, as an instance id refuses it.
    /// </summary>
    [Fact]
    public void AStepsNameEndingInANewLine_IsRefused()
    {
        Assert.Equal(
            ["step 'build\n' is named 'build\n', which is not a lower-case word with dashes within; a read's segments are told apart by dots, so a name may not hold one"],
            Problems("""
                name: flow
                steps:
                  - name: "build\n"
                    type: orient
                """));

        Assert.False(FlowInstanceId.IsWellFormed("build\n"));
    }

    /// <summary>
    /// Every expression ends: one nested past what the grammar's guard allows - a long chain of '!', empty(...) inside
    /// empty(...), any(...) inside any(...), a path of a hundred thousand names - is refused with the depth it went
    /// past, through the grammar and through the validator alike, and never spends the stack of the process that reads it.
    /// </summary>
    [Theory]
    [InlineData("a hundred thousand '!'")]
    [InlineData("a hundred empty(...) inside each other")]
    [InlineData("a hundred any(...) inside each other")]
    [InlineData("a path of a hundred thousand names")]
    public void AnExpressionNestedTooDeep_IsRefused_AndNeverSpendsTheStack(string nested)
    {
        var expression = TooDeep(nested);

        Assert.Equal($"the expression nests more than {FlowExpression.MostNesting} deep", FlowExpression.GrammarProblem(expression));
        Assert.Throws<ArgumentException>(() => FlowExpression.Parse(expression));

        Assert.Equal(
            [$"'gate.when' is not written whole: the expression nests more than {FlowExpression.MostNesting} deep, and nothing past there would be checked"],
            Problems($"""
                name: flow
                steps:
                  - name: plan
                    type: orient
                  - name: gate
                    type: orient
                    dependsOn: [plan]
                    when: '{expression}'
                """));
    }

    /// <summary>An expression nested past the guard by the construct <paramref name="nested"/> names, built here so no test's name carries it.</summary>
    internal static string TooDeep(string nested)
    {
        var any = "plan.ok";

        for (var level = 0; level < 100; level++)
        {
            any = $"any(plan.items, {any})";
        }

        return nested switch
        {
            "a hundred thousand '!'" => new string('!', 100_000) + "plan.ok",
            "a hundred empty(...) inside each other" => string.Concat(Enumerable.Repeat("empty(", 100)) + "plan.ok" + new string(')', 100),
            "a hundred any(...) inside each other" => any,
            "a path of a hundred thousand names" => "plan" + string.Concat(Enumerable.Repeat(".ok", 99_999)),
            _ => throw new ArgumentOutOfRangeException(nameof(nested), nested, "no such nesting"),
        };
    }

    /// <summary>
    /// The grammar itself beside wholeness: what the engine will one day read must parse today, with the column it
    /// stopped at.
    /// </summary>
    [Fact]
    public void AnExpressionThatDoesNotParse_IsRefusedWithItsColumn()
    {
        var problems = Problems("""
            name: flow
            steps:
              - name: gate
                type: orient
                when: 'plan +'
            """);

        Assert.Equal(
            ["'gate.when' is not written whole: column 6: '+' is not part of an expression, and nothing past there would be checked"],
            problems);
    }

    /// <summary>
    /// V29: a fold's allow is held as the request the verb takes - <c>FoldAllowances.Problem()</c> itself - with
    /// its spellings the one wordings, the first problem the request finds written out when the definition is loaded.
    /// </summary>
    [Fact]
    public void AFoldsAllow_IsHeldAsTheRequestTheVerbTakes()
    {
        static IReadOnlyList<string> OneAllow(string body)
            => Problems(
                "name: flow\nsteps:\n  - name: make\n    type: create-agent\n    agent: agents/worker.md\n"
                + "  - name: close\n    type: fold\n    of: make\n    allow:\n" + body);

        Assert.Equal(
            ["step 'close' takes allow: 'src/a.cs' is named with both merge and settled: a path is merged for you, or reconciled by hand, never both."],
            OneAllow("      settled: [src/a.cs]\n      merge: [src/a.cs]\n"));

        Assert.Equal(
            ["step 'close' takes allow: new 'an id' is not an anchor id: an id is not empty, and holds no space and no colon."],
            OneAllow("      new: ['an id']\n"));

        Assert.Equal(
            ["step 'close' takes allow: acceptLost '3:priority' is not <ID>:<cell>, where the id is an anchor's and the cell is trigger, closing, cross-refs."],
            OneAllow("      acceptLost: [3:priority]\n"));

        Assert.Equal(
            ["step 'close' takes allow: acceptOver '4:form' is not <ID>:<cell>, where the id is an anchor's and the cell is priority, status, trigger, closing, cross-refs."],
            OneAllow("      acceptOver: [4:form]\n"));

        // A deletion takes the same allow a fold does, through the same request.
        var dropped = Problems("""
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: agents/worker.md
              - name: close
                type: delete-agent
                of: make
                allow:
                  new: ['an id']
            """);

        Assert.Equal(
            ["step 'close' takes allow: new 'an id' is not an anchor id: an id is not empty, and holds no space and no colon."],
            dropped);
    }

    /// <summary>V29: a refresh's keys are held as the request the verb takes, and a rebase's merge and settled with them.</summary>
    [Fact]
    public void ARefreshsKeysAndARebases_AreHeldAsTheRequestTheVerbTakes()
    {
        var refresh = Problems("""
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: agents/worker.md
              - name: weigh
                type: refresh
                of: make
                paths: [.]
                except: [src/a.cs]
                stale: [src/b.cs]
                merge: [src/a.cs]
            """);

        Assert.Equal(
            [
                "step 'weigh' takes refresh: '.' names the whole tree, not a path in it: say all: true to weigh every path the main tree moved",
            ],
            refresh);

        var twice = Problems("""
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: agents/worker.md
              - name: weigh
                type: refresh
                of: make
                except: [src/a.cs]
                stale: [src/a.cs]
            """);

        Assert.Equal(
            [
                "step 'weigh' takes refresh: 'src/a.cs' is named with both except and stale: a path is left as the agent changed it, or handed the main tree's copy, never both.",
            ],
            twice);

        var rebase = Problems("""
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: agents/worker.md
              - name: move
                type: rebase
                of: make
                settled: [src/a.cs]
                merge: [src/a.cs]
            """);

        Assert.Equal(
            [
                "step 'move' takes a rebase: 'src/a.cs' is named with both merge and settled: a path is merged for you, or reconciled by hand, never both.",
            ],
            rebase);
    }

    /// <summary>
    /// V32's patterns: within and writes are patterns, and a pattern that could name nothing in the tree is a typo,
    /// refused where the definition is loaded.
    /// </summary>
    [Fact]
    public void WithinAndWrites_ArePatternsAPathCouldMatch()
    {
        var problems = Problems("""
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: agents/worker.md
              - name: close
                type: delete-agent
                of: make
                within: ['**x']
            """);

        Assert.Equal(
            ["step 'close' takes within: the pattern '**x' holds '**' inside a segment; '**' is a segment of its own, standing for any number of segments"],
            problems);
    }

    /// <summary>V32's other half, where the key lives: a reason step's written-out writes are patterns, each one a path could match.</summary>
    [Fact]
    public void AReasonStepsWrittenOutWrites_ArePatternsAPathCouldMatch()
    {
        var problems = Problems("""
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: agents/worker.md
              - name: think
                type: reason
                agent: agents/worker.md
                prompt: prompts/think.md
                outputs: schemas/think.json
                access: write
                in: make
                writes: ['**oops']
            """);

        Assert.Equal(
            ["step 'think' names the pattern '**oops' in writes, and it holds '**' inside a segment; '**' is a segment of its own, standing for any number of segments"],
            problems);
    }

    /// <summary>
    /// V41's key rule where the owner cannot see it: a refresh saying <c>all</c> beside a paths EXPRESSION, whose written
    /// paths a request never reads; a written paths beside all is the request's own refusal.
    /// </summary>
    [Fact]
    public void ARefreshSayingAll_BesideAPathsExpression_IsRefused()
    {
        var problems = Problems("""
            name: flow
            inputs:
              - name: paths
                kind: text
            steps:
              - name: make
                type: create-agent
                agent: agents/worker.md
              - name: weigh
                type: refresh
                of: make
                all: true
                paths: input.paths
            """);

        // The input is a text, so the expression is no list either: each refusal is said.
        Assert.Equal(
            [
                "'weigh.paths' yields a text, where a list is read: the key takes a list, written out or as an expression that yields one",
                "step 'weigh' takes refresh: all weighs every path the main tree moved, so it takes no path beside it: give the paths, or all",
            ],
            problems);
    }

    /// <summary>A workflow's own outputs read by expression, and a read that names nothing the flow declares is refused there too.</summary>
    [Fact]
    public void AWorkflowOutputReadingNothing_IsRefused()
    {
        var problems = Problems("""
            name: flow
            steps:
              - name: gate
                type: orient
            outputs:
              - name: done
                from: nowhere.value
            """);

        // The output's key is named as the file names it, 'outputs.<name>.from', and read from the flow's top level.
        Assert.Equal(
            ["'outputs.done.from' reads 'nowhere.value', which no step before 'outputs.done', no input and no param provides"],
            problems);
    }

    /// <summary>
    /// V26: a workflow declares at most 200 steps - groups and their members each one, the recovery's among them, for
    /// they run. Exactly 200 is a flow; 201, however they are split between the flow, its groups and the recovery, is
    /// refused.
    /// </summary>
    [Fact]
    public void AWorkflowDeclaresAtMostTwoHundredSteps()
    {
        Assert.Empty(Problems(Flow(top: WorkflowValidator.MostSteps, recovered: 0)));

        Assert.Equal(
            ["the workflow declares 201 steps; at most 200"],
            Problems(Flow(top: 201, recovered: 0)));

        Assert.Equal(
            ["the workflow declares 201 steps; at most 200"],
            Problems(Flow(top: 150, recovered: 51)));

        // A group and its 199 members are 200 steps, and one more step beside them is the 201st.
        var nested = new StringBuilder("name: flow\nsteps:\n  - name: alone\n    type: orient\n  - name: group\n    steps:");

        for (var index = 0; index < 199; index++)
        {
            nested.Append($"\n      - name: s{index}\n        type: orient");
        }

        Assert.Equal(["the workflow declares 201 steps; at most 200"], Problems(nested.ToString()));

        // A flow of top-level steps, the first of which gives its failure to a recovery of so many steps.
        static string Flow(int top, int recovered)
        {
            var text = new StringBuilder("name: flow\nsteps:");

            for (var index = 0; index < top; index++)
            {
                text.Append($"\n  - name: s{index}\n    type: orient");

                if (index == 0 && recovered > 0)
                {
                    text.Append("\n    onFailure: recover");
                }
            }

            if (recovered > 0)
            {
                text.Append("\nrecover:\n  onSuccess: failed-step\n  onFailure: pause\n  steps:");

                for (var index = 0; index < recovered; index++)
                {
                    text.Append($"\n    - name: r{index}\n      type: orient");
                }
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// Every problem of a definition whose files are <paramref name="files"/>, by the paths it names them with: a reason
    /// step's output typed by its schema, and each prompt read for what it reads.
    /// </summary>
    private static IReadOnlyList<string> Problems(string text, IReadOnlyDictionary<string, string> files)
        => [.. WorkflowValidator.Validate(Parse(text), hasCommitTemplate: false, path => files.GetValueOrDefault(path))];

    /// <summary>
    /// V16-V19's types, for a built-in step: a path is typed by the output part 04 lists for its type, and one no
    /// output holds is refused - a member it does not name, a member of a text, an index into one, and a read of a type
    /// that has no output at all. The engine's facts of a step, an input and the run's own names are typed as what they
    /// are: an input is a text, the run holds the values an expression reads and the texts a template does, and a work
    /// item is an item of what its group's forEach yields - a plan check's items being what its own items key yields.
    /// A block's gathered list is a list of what its members' lists hold; a group that gathers nothing has no output.
    /// </summary>
    [Fact]
    public void APathNoOutputHolds_IsRefused_AsIsANameTheRunHasNot()
    {
        Assert.Equal(
            [
                "'a.when' reads 'orient.hasLegz', and the output of step 'orient', an orient step, has no property 'hasLegz'",
                "'b.when' reads 'orient.branch.name', and the output of step 'orient', an orient step, says 'orient.branch' is a text, which has no property 'name'",
                "'c.when' reads 'orient.branch[0]', and the output of step 'orient', an orient step, says 'orient.branch' is a text, which is no list to index",
                "'d.when' reads 'make.ready', and step 'make', a create-agent step, has no output to read",
                "'e.when' reads 'check.code.x', and the engine's fact 'code' of step 'check' says 'check.code' is a whole number, which has no property 'x'",
                "'f.when' reads 'input.request.x', and input 'request' says 'input.request' is a text, which has no property 'x'",
                "'g.when' reads 'run.statuz', and the run has no 'statuz': an expression reads run.name, run.status, run.done, run.failed, run.paused, run.stopped, run.passedOver, and a template run.ran, run.notRun, run.red, run.leftover, run.assumptions, run.preExisting too",
                "'h.when' reads 'run.ran', which is text for a template, not a value for an expression: the engine writes it from its step records for a person to read",
                "'items.inner.when' reads 'item.path', and the work item of group 'items' says 'item' is a text, which has no property 'path'",
                "'i.when' reads 'checks.found', and group 'checks' holds no step 'found' and gathers none of that name",
                "'i.when' reads 'it.x', and each item of 'checks.findings' says 'it' is a text, which has no property 'x'",
                "'j.when' reads 'items.x', and group 'items' holds no step 'x' and gathers nothing",
            ],
            Problems(
                """
                name: flow
                inputs:
                  - name: request
                steps:
                  - name: orient
                    type: orient
                  - name: make
                    type: create-agent
                    agent: worker
                  - name: check
                    type: validate-plan
                    items: 'orient.dirtyPaths'
                  - name: a
                    type: orient
                    when: 'orient.hasLegz'
                  - name: b
                    type: orient
                    when: "orient.branch.name == 'x'"
                  - name: c
                    type: orient
                    when: "orient.branch[0] == 'x'"
                  - name: d
                    type: orient
                    when: 'make.ready'
                  - name: e
                    type: orient
                    when: 'check.code.x'
                  - name: f
                    type: orient
                    when: "input.request.x == 'y'"
                  - name: g
                    type: orient
                    when: 'run.statuz'
                  - name: h
                    type: orient
                    when: "run.ran == 'x'"
                  - name: items
                    forEach: 'check.items'
                    steps:
                      - name: inner
                        type: orient
                        when: "item.path == 'x' && check.ok"
                  - name: checks
                    gather: [findings]
                    steps:
                      - name: balance
                        type: anchor-balance
                      - name: citations
                        type: anchor-citations
                  - name: i
                    type: orient
                    when: "checks.found || any(checks.findings, it.x == 'y')"
                  - name: j
                    type: orient
                    when: 'items.x'
                """));
    }

    /// <summary>
    /// A reason step's output is typed by the schema file it names, where the files are given (part 04, "Checked
    /// against files"): a property no schema holds is refused, at the root and inside a list's items, and so is a member
    /// of a text, its newest output (latest) typed as the step is; a work item a forEach hands its steps is an item of the
    /// list the plan check checked. A schema the subset does not read types nothing, and without the files a reason
    /// step's output may be anything.
    /// </summary>
    [Fact]
    public void AReasonStepsOutput_IsTypedByTheSchemaItNames()
    {
        const string definition = """
            name: flow
            steps:
              - name: plan
                type: reason
                agent: planner
                prompt: prompts/plan.md
                outputs: schemas/plan.json
              - name: check
                type: validate-plan
                items: 'plan.items'
              - name: items
                forEach: 'check.items'
                steps:
                  - name: build
                    type: orient
                    when: "item.ready && plan.itemz == null && plan.items[0].titel == 'x' && plan.latest.summary.x == 'y' && item.titel == 'x'"
              - name: loose
                type: reason
                agent: planner
                prompt: prompts/plan.md
                outputs: schemas/unread.json
              - name: after
                type: orient
                when: 'loose.anything'
            """;

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prompts/plan.md"] = "Plan the work.\n",
            ["schemas/plan.json"] = """
                {"type":"object","additionalProperties":false,"properties":{
                  "summary":{"type":"string"},
                  "items":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"title":{"type":"string"},"ready":{"type":"boolean"}}}}}}
                """,
            ["schemas/unread.json"] = """{"type":"object"}""",
        };

        Assert.Equal(
            [
                "'items.build.when' reads 'plan.itemz', and the schema 'schemas/plan.json' of step 'plan' has no property 'itemz'",
                "'items.build.when' reads 'plan.items[0].titel', and the schema 'schemas/plan.json' of step 'plan' has no property 'titel' in 'plan.items[0]'",
                "'items.build.when' reads 'plan.latest.summary.x', and the schema 'schemas/plan.json' of step 'plan' says 'plan.latest.summary' is a text, which has no property 'x'",
                "'items.build.when' reads 'item.titel', and the work item of group 'items' has no property 'titel'",
            ],
            Problems(definition, files));

        Assert.Empty(Problems(definition));
    }

    /// <summary>
    /// What each key reads, its expression yields (part 04): a when, an until, a blocking and a pre-answer's when a
    /// boolean; a forEach, a pause's questions and every key that takes a list a list; a quote the words it looks for, a
    /// text. A leftover takes anything. A value that can be none of what is read is refused, named by its kind.
    /// </summary>
    [Fact]
    public void EachKeysExpression_YieldsWhatTheKeyReads()
    {
        Assert.Equal(
            [
                "'pick.when' yields a text, where a boolean is read: nothing is read as true or as false but true and false",
                "'pick.questions' yields a boolean, where a list is read: the step shows each question it holds under its own",
                "'pick.preAnswer.when' yields a text, where a boolean is read: nothing is read as true or as false but true and false",
                "'pick.preAnswer.quote.text' yields a list, where a text is read: a quote is the words looked for in what the person gave",
                "'each.forEach' yields a whole number, where a list is read: the group runs its steps once for each of its items",
                "'loop.repeat.until' yields a whole number, where a boolean is read: nothing is read as true or as false but true and false",
                "'fold.within' yields a text, where a list is read: the key takes a list, written out or as an expression that yields one",
            ],
            Problems(
                """
                name: flow
                inputs:
                  - name: request
                steps:
                  - name: orient
                    type: orient
                  - name: pick
                    type: pause
                    question: 'pick'
                    options: [go]
                    when: 'orient.branch'
                    questions: 'orient.hasLegs'
                    preAnswer:
                      - when: 'input.request'
                        answer: go
                        quote:
                          text: 'orient.tests'
                          input: request
                  - name: each
                    forEach: 'orient.legCount'
                    steps:
                      - name: inner
                        type: orient
                  - name: loop
                    repeat:
                      until: 'inner.legCount'
                      maxIterations: 2
                      leftover: 'inner.tests'
                    steps:
                      - name: inner
                        type: orient
                  - name: make
                    type: create-agent
                    agent: worker
                  - name: fold
                    type: fold
                    of: make
                    within: 'orient.branch'
                """));
    }

    /// <summary>
    /// Each operator of an expression is held to what it takes, said after the key it is read in: '!' and the joins a
    /// boolean, a comparison no list and no object, any a list. A path a refusal already named is not typed again.
    /// </summary>
    [Fact]
    public void AnExpressionsOperators_AreHeldToWhatTheyTake_NamingTheKey()
    {
        Assert.Equal(
            [
                "'gate.when' applies ! to 'orient.branch', which is a text; ! takes a boolean, and nothing is read as true or as false but true and false",
                "'gate.when' joins 'orient.legCount', which is a whole number, with &&; && joins booleans, and nothing is read as true or as false but true and false",
                "'gate.when' compares 'orient.tests', which is a list, with ==; == compares two scalars, or null, and a list or an object is never one",
                "'gate.when' looks into 'orient.branch' with any, and it is a text; any looks into a list",
                "'later.when' reads 'ghost.ok', which no step before 'later', no input and no param provides",
            ],
            Problems(
                """
                name: flow
                steps:
                  - name: orient
                    type: orient
                  - name: gate
                    type: orient
                    when: "!orient.branch && orient.legCount && orient.tests == null && any(orient.branch, true)"
                  - name: later
                    type: orient
                    when: "ghost.ok && orient.branch"
                """));
    }

    /// <summary>
    /// How the run stands without its always steps - run.status, done, failed, paused and stopped - is read only by an
    /// always step or a member of an always group, so that none reads its own effect; its name and how many steps were
    /// passed over are read anywhere. A template reads it under the same rule, and so does a workflow's output, which is
    /// no step.
    /// </summary>
    [Fact]
    public void HowTheRunStands_IsReadOnlyByAnAlwaysStep()
    {
        const string Why = "how the run stands without its always steps, and '{0}' is neither an always step nor in an always group; only an always step reads how the rest of the flow came to, so that none reads its own effect";

        Assert.Equal(
            [
                $"'early.when' reads 'run.stopped', {string.Format(System.Globalization.CultureInfo.InvariantCulture, Why, "early")}",
                $"'note.message' reads 'run.status', {string.Format(System.Globalization.CultureInfo.InvariantCulture, Why, "note")}",
                $"'outputs.state.from' reads 'run.paused', {string.Format(System.Globalization.CultureInfo.InvariantCulture, Why, "outputs.state")}",
            ],
            Problems(
                """
                name: flow
                steps:
                  - name: start
                    type: orient
                  - name: report
                    type: orient
                    always: true
                    when: "run.failed || run.status == 'failed'"
                  - name: closing
                    always: true
                    steps:
                      - name: inner
                        type: orient
                        when: 'run.done && run.passedOver == 0'
                  - name: early
                    type: orient
                    dependsOn: [start]
                    when: "run.stopped && run.name == 'x' && run.passedOver == 0"
                  - name: note
                    type: commit
                    dependsOn: [early]
                    message: 'run {run.name}: {run.status}'
                outputs:
                  - name: state
                    from: 'run.paused'
                """));
    }

    /// <summary>
    /// A pull request's base reads nothing but the run's inputs and run.name, since orient fills orient.baseBranch with
    /// it before any other step has run; and every pull request of the flow names the same base - one written, or none,
    /// the remote's default branch - since orient.baseBranch is one branch.
    /// </summary>
    [Fact]
    public void APullRequestsBase_ReadsTheRunsInputsAlone_AndIsOneForEveryPullRequest()
    {
        Assert.Equal(
            [
                "step 'third' opens its pull request against {orient.branch}: a pull request's base reads nothing but the run's inputs and run.name, so that orient, the first step, fills orient.baseBranch with it before any other step has run",
                "steps 'first' and 'second' open pull requests against different bases, '{input.target}' and 'release/{run.name}'; orient.baseBranch is one branch, which every step of the flow reads",
                "steps 'first' and 'third' open pull requests against different bases, '{input.target}' and '{orient.branch}'; orient.baseBranch is one branch, which every step of the flow reads",
                "steps 'first' and 'fourth' open pull requests against different bases, '{input.target}' and the remote's default branch; orient.baseBranch is one branch, which every step of the flow reads",
            ],
            Problems(
                """
                name: flow
                inputs:
                  - name: target
                steps:
                  - name: orient
                    type: orient
                  - name: first
                    type: pull-request
                    dependsOn: [orient]
                    title: 'one'
                    body: prompts/body.md
                    base: '{input.target}'
                  - name: second
                    type: pull-request
                    dependsOn: [first]
                    title: 'two'
                    body: prompts/body.md
                    base: 'release/{run.name}'
                  - name: third
                    type: pull-request
                    dependsOn: [second]
                    title: 'three'
                    body: prompts/body.md
                    base: '{orient.branch}'
                  - name: fourth
                    type: pull-request
                    dependsOn: [third]
                    title: 'four'
                    body: prompts/body.md
                """));

        Assert.Empty(Problems(
            """
            name: flow
            inputs:
              - name: target
            steps:
              - name: first
                type: pull-request
                title: 'one'
                body: prompts/body.md
                base: 'release/{input.target}'
              - name: second
                type: pull-request
                dependsOn: [first]
                title: 'two'
                body: prompts/body.md
                base: 'release/{input.target}'
            """));
    }

    /// <summary>
    /// A prompt's own placeholders - a reason step's prompt and resumePrompt, a pull request's body - are checked against
    /// what the step can read from where it sits, each said at the line it first stands on: a name nothing provides, a
    /// path no schema holds, and in a prompt, which the first iteration is asked with too, the iteration before; a
    /// resumePrompt reads it. A prompt not written whole is refused, and a skill it names is looked for by whoever reads
    /// the skills. Without the files, no prompt is read.
    /// </summary>
    [Fact]
    public void APromptsPlaceholders_AreReadAsItsStepReads()
    {
        const string definition = """
            name: flow
            inputs:
              - name: request
            steps:
              - name: plan
                type: reason
                agent: planner
                prompt: prompts/plan.md
                outputs: schemas/plan.json
              - name: rounds
                dependsOn: [plan]
                repeat:
                  until: 'draft.ok'
                  maxIterations: 2
                steps:
                  - name: draft
                    type: reason
                    agent: planner
                    resume: draft
                    prompt: prompts/draft.md
                    resumePrompt: prompts/again.md
                    outputs: schemas/plan.json
              - name: pr
                type: pull-request
                dependsOn: [rounds]
                title: 'done'
                body: prompts/body.md
            """;

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schemas/plan.json"] = """{"type":"object","additionalProperties":false,"properties":{"summary":{"type":"string"},"ok":{"type":"boolean"}}}""",
            ["prompts/plan.md"] = "Plan {input.request}.\n{skill.notes}\nBefore: {previous.draft.summary}\n",
            ["prompts/draft.md"] = "Draft from {plan.summary} and {plan.sumary}.\nIteration {iteration}, after {previous.draft.summary}.\nAgain {plan.sumary}.\n",
            ["prompts/again.md"] = "Again, after {previous.draft.summary}.\nAnd {later.x}.\n",
            ["prompts/body.md"] = "Body {rounds.draft.summary}\n{oops\n",
        };

        Assert.Equal(
            [
                "step 'plan' prompt 'prompts/plan.md' line 3 reads 'previous.draft.summary': a prompt is what the first iteration is asked with too, which has none before it; the iteration before is read in a resumePrompt",
                "step 'rounds.draft' prompt 'prompts/draft.md' line 1 reads 'plan.sumary', and the schema 'schemas/plan.json' of step 'plan' has no property 'sumary'",
                "step 'rounds.draft' prompt 'prompts/draft.md' line 2 reads 'previous.draft.summary': a prompt is what the first iteration is asked with too, which has none before it; the iteration before is read in a resumePrompt",
                "step 'rounds.draft' resumePrompt 'prompts/again.md' line 2 reads 'later.x', which no step before 'rounds.draft', no input and no param provides",
                "step 'pr' body 'prompts/body.md' is not written whole: the placeholder at column 1 of line 2 is never closed, and nothing it reads past there would be checked",
            ],
            Problems(definition, files));

        Assert.Empty(Problems(definition));
    }
}
