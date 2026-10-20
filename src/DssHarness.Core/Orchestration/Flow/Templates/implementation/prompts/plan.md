Plan the work of this run.

## The request

{input.request}

Where nothing stands above this line, the run's prioritiser chose the work: {intake.priority.request}

Where it chose no work, what the person decided, below, is the request.

## What triage found

- Title: {intake.triage.title}
- Kind: {intake.triage.kind}
- Summary: {intake.triage.summary}
- Touches: {intake.triage.touches}
- What the request itself asks to have refactored, rewritten or created again: {intake.triage.rebuilds}
- Still ambiguous: {intake.triage.ambiguities}
- Assumed without asking: {intake.triage.assumptions}

## A plan that came with the request

{intake.triage.plan}

A plan given to the run as a file: `{input.plan}`. An empty name means none.

Where either is there, read it and build on it: keep to it, turn it into items, and list each departure with its
reason. Where both are there, the file given to the run is the plan and the other is context. Where there is none, the
plan is yours to make, and it has to be thorough.

## What stands in the way

Blockers, the most pressing first, which you plan first: {intake.blockers}

Red legs of CI: {ci.red}

Red legs of the baseline: {baseline.red}

What only a person could lift, as it was put to them, each with its choices and the one recommended: {intake.stops}

What the person decided about it: {blocked.text}

An empty list means nothing stood in the way and nobody was asked. Where the person decided something, the plan
holds to it.

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.

## How the tests are run

Each item names the filter that selects the tests its change affects: the test classes that test what the item
changes, the tests it writes among them - or, for a runner that selects tests by name, those tests' names. Write
test classes, not the classes the item changes: a runner selects tests, and which tests reach a class is not known
from the class. The engine tests each item on its filter alone where the run's input affectedTests is on (it is
`{input.affectedTests}`), and every test runs once every item of the round is built; where it is off, every test runs
for each item, and the filter is kept for a run that turns it on. The legs this run tests on, each with its test
runner as the repository's settings give it, the option a filter is given through, and what the leg leaves out:

{orient.tests}

The filter is given to the runner after that option, as one argument, exactly as you write it: write it in what that
option takes for that runner. One filter is given to every leg the item is tested on, so it has to mean the same tests
to each. It may name only tests that are there or that the item writes. One that a leg's own exclusions leave nothing
of is refused before anything is built, and a person is asked. Left empty, there is no filter: every test runs for the
item.

## How recommendations are made in this repository

{recommendations}

## What to do

1. Read the code the work touches until you can say what has to change.
2. Before you design anything, search the repository for what already does this work or a part of it: types,
   services, helpers, conventions, tests. Plan the work as an extension of what you find. Create something from
   nothing only where the repository has no machinery related to what is needed, and then say what you searched for
   and where.
3. What triage lists as asked to be rebuilt is the one exception: replacing exactly that is the work. Reuse everything
   else, and remove what you replace in this plan; where that list is empty, nothing that exists is rebuilt.
4. Where something is open that changes the design, your run context says whether you may ask the person. Ask now,
   everything at once, each question with its choices and the one you recommend by the bar above; or decide, and
   record each assumption.
5. Design the work, then split it into at most 8 items by sets of files, as your instructions say. Size each item to
   one builder's cycle: as much work as one agent builds whole in a single sitting, with its tests - never more than
   that, and never a sliver of one; an item too small to fill a builder is merged with the work beside it. Items are
   built at the same time: none may need another's result. Leave nothing inside an item that you already know only a
   person can give: ask it now, or, where you may not, leave it out and name it in the summary. A builder that finds
   part of its item cannot be done says so, and a person is asked before anything of the item is folded.
6. For each item write: instructions a builder with none of this conversation can follow; its definition of done, as
   statements a check can answer one by one - each behaviour, the test that pins it, and what is true of the tree
   when the builder reports; the paths it owns and the paths it must not touch; its test filter; what it reuses, by
   path and for what; what it creates, with why nothing that exists serves and where you searched; and what it
   replaces because the request asked, by path, with what becomes of the old one.

## What to return

A summary of the plan, naming each part of the request no item covers, with the reason; the plan's definition of
done - what holds when the whole plan is built, as statements a check can answer one by one, which the run's final
audit holds every round against; whether it was built on a plan a person gave or made here; each departure from a
given plan, with its reason; the items; and under `scope` an empty list, since nothing is being updated.

A machine checks the items before anyone builds them: every name valid, no file under two items, forbidden paths kept,
every path an item reuses or replaces there in the tree, and every item saying what it reuses, creates or replaces.
An independent auditor then checks the plan against the code, searching for itself what you say does not exist. Then
a person approves it, unless they approved it beforehand in what they gave this run.
