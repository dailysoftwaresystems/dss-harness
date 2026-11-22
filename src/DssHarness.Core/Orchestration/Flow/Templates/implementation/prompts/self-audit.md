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

Where this is past the first round, the item audit searched the data for what these items' classes decide, before they
were built. Its findings, with the sites it found that the items' site lists lacked and the cells of their universes
they left out:

{item-audit.latest.findings}

{item-audit.latest.closure}

Where a line says that the item audit left no output, it was skipped in the first round, whose items were the plan's
own and were audited with the plan; or it failed, or a person had it passed over, and what it would have found is
unknown - say so under unverified, never as a pass.

The work is in the main tree, committed; `git diff {orient.startCommit} HEAD` shows all of it.

## What the engine recorded

- What was checked, and how each check ended:

{run.ran}

- What was not checked:

{run.notRun}

- What is known to be red:

{run.red}

- The gate on the main tree: the tests the run's work affects, or every test where the run's input finalTests says
  all (it is `{input.finalTests}`). Passed: {gate.passed}. Red legs:

{gate.red}

- The mutation sweep, which proves the tests can fail. Passed: {mutations.passed}. Arms that are not as they should be:

{mutations.red}

- The registry's checks on the round: {round-checks.findings}
- What the review loops left: {run.leftover}
- What was assumed without asking a person: {run.assumptions}
- The issues this run owes its next round, each with its id and its size - the files its fix touches, what must be
  built, and what must be measured first:

{run.nextRound}

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.
Name it under unverified; it is a finding only where the line says the step failed, not where it was skipped.

## What to do

1. Hold the request, the plan's definition of done and each item of this round against the tree, statement by
   statement: is each part there, and is it what was asked? A statement that does not hold is a finding, even where the records name it as left, but for an item a person
   skipped.
2. Look for what was left out without being said. An item a person chose to skip shows above among what was not
   checked: it is not a finding and gets no work item.
3. A red gate, an arm of the sweep that is not as it should be and a finding of the registry's checks are findings,
   and so is each finding of the item audit above that the tree does not settle: a site it found that still decides on
   its own, a cell of the universe still left out. Give every finding a work item that repairs it, unless only a
   person can: say so in the finding, and give it no item.
4. Each issue the run owes its next round, listed above, becomes the first work item of that round: make it one, with
   its size and a class - it is a defect too, and the plan's rules of a class apply to it - and name the issue it
   settles in `settles`, by its id exactly. The round's check refuses a round whose items leave one unsettled, and the
   rounds do not end while one is.
5. Say unverified of what you could not verify.

## The tests a work item names

Each item names the tests its change affects in tests, one unit to an entry - the test classes that test what it
changes, the tests it writes among them - each written in what every leg's test runner takes after its filter option;
or, where its change affects no test, leaves tests empty and says why in noTests. The engine tests an item on its
units alone, with any its builder adds, and on none where it names none. The legs this run tests on, each with its
runner, the option a filter is given through, and what the leg leaves out:

{orient.tests}

A unit may name only tests that are there or that the item writes, and none a leg leaves out. An item whose change
reaches code a test reads names that test: a noTests whose reason does not hold is a finding, and the work item
that repairs it names the tests.

## What to return

Whether the round is clean, which is true exactly when you give no work item; the findings; what you could not
verify; and the work items of a further round, shaped as the plan's items are, as many as the findings need: each sized to one
builder's cycle, never more and never a sliver of one, each with its own definition of done - statements a check can
answer one by one, the first of them the finding it settles - and each that changes a decision with its classes, or
saying why in noClass where it decides none. An item that settles an issue the run owes names it in `settles`. Each takes a name no work item of this run has had, in this round or an
earlier one: the run knows a work item by its name, and keeps its tests and its updates under it, so a further item
named as one already built would stand in that item's place - a follow-up to an item is a work item of its own, named
for what it settles. Where the item audit found a site an item's class lacked or a cell its
universe left out, the item that settles it carries the class whole: its site list extended by what the audit found.
