Audit this plan before it is built.

## What was asked

{input.request}

Where nothing stands above this line, the run's prioritiser chose the work. Triage's summary of it:
{intake.triage.summary}

Where it chose no work, what the person decided, below, is what was asked.

A plan the request brought, as triage found it: {intake.triage.plan}

A plan given to the run as a file: `{input.plan}`. An empty name means none.

What triage assumed without asking: {intake.triage.assumptions}

What the request itself asks to have refactored, rewritten or created again, by triage: {intake.triage.rebuilds}

What stands in the way, by triage and the prioritiser: {intake.blockers}

What only a person could lift, as it was put to them: {intake.stops}

What the person decided about it: {blocked.text}

An empty list means nothing stood in the way and nobody was asked.

CI. The matrix ran: {ci.matrixRan}; where it did not, CI verified nothing. Red legs: {ci.red}

Red legs of the baseline: {baseline.red}

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.

## The plan

Summary: {plan.latest.summary}

The plan's own definition of done: {plan.latest.done}

Built on a plan a person gave (`given`) or made here (`made`): {plan.latest.source}

Departures from the given plan: {plan.latest.departures}

What the planner assumed without asking: {plan.assumptions}

Items:

{plan.latest.items}

## How the tests are run

The legs this run tests on, each with its test runner, the option a filter is given through, and what the leg leaves
out:

{orient.tests}

Each item's filter must mean the same tests on every leg, and name only tests that are there or that the item writes.

## Where a plan update is under way

{update.cause}

Where the lines above say that none is under way, skip this section. Otherwise this plan replaces one that was
approved, because a person changed something while the run worked. Check also: the plan does all the change asks and
nothing more; what the change does not touch is as it was; each item added, changed or withdrawn has exactly one
piece of that kind, and no other item has one; a piece for work that was built says what is taken back and what
stays; and nothing to be built, done again or taken back is left to a `told` piece.

What the plan says the change does to the scope: {plan.latest.scope}

The work of this run: {update.work}

## What to do

1. Check the plan's claims about the code, in the code.
2. Check what each item reuses: open each path it names, and see that it is what the item says and can carry what the
   item puts on it.
3. Check what each item creates, by searching the repository yourself for machinery that already does it or a part of
   it. The search the item reports is a claim. A plan that builds what exists, or gives a rule a second owner, does
   not pass.
4. Check what each item replaces against what the request, or a change under way, asks to have rebuilt: exactly
   that, nothing beyond it, and the old one removed.
5. Check that every part of what was asked is in an item, or is named in the summary as left out, with a reason that
   holds.
6. Check each item's definition of done, and the plan's own: every statement concrete and checkable - a behaviour and
   what pins it, never prose a check cannot answer - each item's covering the whole of its instructions, and the
   plan's covering the whole of what was asked. And check the sizing: no item carrying more than one builder's cycle
   of work, and none a sliver of one that belongs to the work beside it.
7. Read each item as its builder will, and each test as someone who wants it to fail.
8. Where a person gave a plan, check their plan against the items and the departures, as your instructions say.
9. Weigh each assumption: is it one a careful colleague would make, or one that should have been a question?
10. Where a person decided something when the run stopped to ask them, check that the plan holds to it.

## What to return

Go, or the findings; and what you checked. Go means the plan can be built as it stands.
