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

Its items:

{planning.plan.latest.items}

This is round {iteration}. The work items of this round, as the plan check that opened it passed them - the plan's in
the first round, those an earlier audit gave in a later one, with those a change of plan placed in it:

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

## What to do

1. Hold the request and each item of this round against the tree: is each part there, and is it what was asked?
2. Look for what was left out without being said. A work item a person chose to leave out, when its builder said it
   could not do it, is said: it shows above among what was not checked, with that reason. It is no finding and gets
   no work item: a person decided it, and the run's report lists it.
3. A red gate, an arm of the sweep that is not as it should be and a finding of the registry's checks are findings.
   Give each a work item that repairs it.
4. Say unverified of what you could not verify.

## The tests a work item names

Each item names the filter that selects its tests, written in what each leg's test runner takes after its filter
option. The legs this run tests on, each with its runner, the option a filter is given through, and what the leg
leaves out:

{orient.tests}

A filter may name only tests that are there or that the item writes, and none a leg leaves out. Empty is no filter:
every test runs.

## What to return

Whether the round is clean; the findings; what you could not verify; and, where it is not clean, the work items of a
further round, shaped as the plan's items are and at most 8.
