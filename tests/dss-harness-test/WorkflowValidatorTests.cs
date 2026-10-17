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
        => WorkflowFileParser.Parse("workflows/implementation/default.yml", text);

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
                "step 'folding' names 'implement' as its of, which is a reason step; an agent is made by a create-agent step",
                "step 'folding-wrong' names 'reads-but-names-in' as its of, which is a reason step; an agent is made by a create-agent step",
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
                  when: "last.ok && source.code != 13"
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
                "the workflow declares recover, and no step gives it a failure; a step says onFailure: recover, itself or through a group around it, for its failures to reach it",
                "'reads-recovery.when' reads 'recover.diagnose.ok': the recovery's steps are read by their own names inside it, and nothing outside it reads them",
                "'reads-source.when' reads 'source.code', whose 'source' names the step whose ending raised a handler, and 'reads-source' is neither a handler, nor in one, nor a step of the recovery",
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
                "step 'work.plan' is a reasoning step, and its failure would go to the recovery by the onFailure of 'work'; a reasoning step is never recovered - say pause on it",
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
                  - name: work
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
              - name: orient
                type: orient
                answers:
                  failure: retry
                  ask: decide
                  runBudget: more
                  pause: go
                  contextLimit: skip
                  checksPending:
                    answer: accept
                    times: 2
                  visitBound:
                    answer: more
                    then: accept
              - name: baseline
                type: test
                skippable: true
                answers:
                  truncated:
                    answer: retry
                    times: 2
                    then: skip
            """);

        Assert.Equal(
            [
                "the workflow answers 'foldLostRows': a lost or an over cell at a fold is a person's to accept, and no line of a configuration accepts it",
                "the workflow answers 'loopBound' with 'bogus', which it does not offer. Its answers: 'more', 'accept', 'abort'.",
                "the workflow: the times of the answer to 'turnLimit' must be from 1 to 20, found 50",
                "the workflow answers 'turnLimit' then 'retry', which is not one of its answers that end the matter: 'skip', 'fail', 'abort'",
                "step 'orient' answers 'failure': what a step's failure does is its onFailure, which answers that question beforehand",
                "step 'orient' answers 'ask': whether a step may put its questions to a person is the step's own ask",
                "step 'orient' answers 'runBudget': the run's budget is the workflow's, and only the workflow's own answers answer for it",
                "step 'orient' answers 'pause': a pause step's question is answered on that step alone",
                "step 'orient' answers 'contextLimit' with skip, and 'orient' is no skippable step; skip ends a step skipped only where it says skippable",
                "step 'orient' answers 'checksPending' with accept 2 times; times count an answer after which the same question can come again - more, retry or compact - and accept ends the matter",
                "step 'orient' answers 'visitBound' then accept with no times; then is what is answered once the counted answers are spent",
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
              - name: work
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
                "group 'work': retries must be from 1 to 20, found -1",
                "group 'work' says maxVisits without on; it bounds how often a handler is raised, and the schedule starts a step once",
                "step 'work.self' is raised by its own ending, and so calls itself; a step never does - one that is to run again after it failed says retries",
                "step 'work.codes' is raised by the codes 300, 20, 20, and an exit code is from 1 to 255",
                "step 'work.codes' is raised by the code 20 more than once",
                "step 'work.codes': maxVisits must be from 1 to 20, found 21",
                "step 'work.succeeded' is raised by a success with codes; a step that ended done ended with 0, and codes are a failure's",
                "step 'work.succeeded' is raised by 'build' and also depends on 'build': a handler waits for its source's ending and for nothing else",
                "step 'work.succeeded' is raised by 'build' and is always; an always step is started by the end of the session, never by a step",
                "step 'work.succeeded' sends the run back to itself, and so calls itself; a step never does - one that is to run again after it failed says retries",
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
            ],
            problems);

        var unbounded =
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
            """;

        Assert.Equal(["group 'loop': repeat.blockersLimitRounds is false and the workflow has no budget: nothing would bound the loop"], Problems(unbounded));
        Assert.Empty(Problems(unbounded.Replace("steps:\n  - name: loop", "budget:\n  usd: 5\nsteps:\n  - name: loop", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A read names the nearest step it sees before the flow's root paths: a sibling declared before it wins over a
    /// root step of the same name declared later. And only a reason step's work has a latest output, or assumptions.
    /// </summary>
    [Fact]
    public void ARead_NamesTheNearestStepFirst_AndOnlyAReasonStepHasALatest()
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
              - name: a
                type: orient
              - name: think
                type: reason
                agent: thinker
                prompt: prompts/think.md
                outputs: schemas/think.json
              - name: c
                type: orient
                when: '!empty(think.latest.blocked) && empty(think.assumptions)'
            """);

        Assert.Equal(
            ["'group.b.when' reads 'a.latest.blocked', and 'group.a' is a orient step: only a reason step's work has a latest output"],
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
            """);

        Assert.Equal(
            [
                "step 'both' names 'of' and 'others'; it works on one agent, or on every other one, and never both",
                "step 'neither' names neither 'of' nor 'others'; it works on one agent, or on every live one but the agent 'others' names",
                "step 'wrong' names 'neither' as its others, which is a rebase step; an agent is made by a create-agent step",
                "step 'dropped' discards what is not committed and says what its fold may carry; a deletion that discards folds nothing, so allow and within would be read by nothing",
                "step 'reads' names writes and only reads; a step that writes says access: write and names the create-agent step it works in",
            ],
            problems);
    }
}
