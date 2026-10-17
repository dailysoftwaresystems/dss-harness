Audit this round of the run. It says it is finished.

## What was asked

{input.request}

Where nothing stands above this line, the run's prioritiser chose the work. Triage's summary of it:
{intake.triage.summary}

What the request itself asked to have refactored, rewritten or created again: {intake.triage.rebuilds}

Everything else was to be built on what the repository has: each item names what it reuses, what it creates and what
it replaces.

## What the run says it did

The plan: {planning.plan.latest.summary}

The plan's definition of done, which this round is held against statement by statement:

{planning.plan.latest.done}

Its items:

{planning.plan.latest.items}

This is round {iteration}. This round's work items, as its plan check passed them: the plan's in round 1, the last
audit's after that, with any a change of plan added:

{item-check.items}

The work is in the main tree, committed; `git diff {orient.startCommit} HEAD` shows all of it.

## What the engine recorded

- What was checked, and how each check ended:

{run.ran}

- What was not checked:

{run.notRun}

- What is known to be red:

{run.red}

- The gate, the whole of the repository's tests on the main tree. Passed: {gate.passed}. Red legs:

{gate.red}

- The mutation sweep, which proves the tests can fail. Passed: {mutations.passed}. Arms that are not as they should be:

{mutations.red}

- The registry's checks on the round: {round-checks.findings}
- What the review loops left: {run.leftover}
- What was assumed without asking a person: {run.assumptions}

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.
Name it under unverified; it is a finding only where the line says the step failed, not where it was skipped.

## What to do

1. Hold the request, the plan's definition of done and each item of this round against the tree, statement by
   statement: is each part there, and is it what was asked? A statement that does not hold is a finding, even where the records name it as left, but for an item a person
   skipped.
2. Look for what was left out without being said. An item a person chose to skip shows above among what was not
   checked: it is not a finding and gets no work item.
3. A red gate, an arm of the sweep that is not as it should be and a finding of the registry's checks are findings.
   Give every finding a work item that repairs it, unless only a person can: say so in the finding, and give it no
   item.
4. Say unverified of what you could not verify.

## The tests a work item names

Each item names the filter that selects its tests, written in what each leg's test runner takes after its filter
option. The legs this run tests on, each with its runner, the option a filter is given through, and what the leg
leaves out:

{orient.tests}

A filter may name only tests that are there or that the item writes, and none a leg leaves out. Empty is no filter:
every test runs.

## What to return

Whether the round is clean, which is true exactly when you give no work item; the findings; what you could not
verify; and the work items of a further round, shaped as the plan's items are and at most 8: each sized to one
builder's cycle, never more and never a sliver of one, and each with its own definition of done - statements a check
can answer one by one, the first of them the finding it settles.
