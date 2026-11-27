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

## What this run owes its next round

The issues this run owes its next round, each with its id and its size - the files its fix touches, what must be
built, and what must be measured first:

{run.nextRound}

An empty list means none is owed. Each issue listed becomes a work item of the plan's first round, listed and started
first, built beside the rest - no other item may need its result. Make it one built to the issue's size - the files its
fix touches among the paths it owns, what must be built and measured first in its instructions - with its class, for
it is a defect too and the rules of a class below apply to it, or, where its fix decides nothing, why in noClass; and
name the issue it settles in `settles`, by its id exactly. The round's check refuses a round whose items leave one
unsettled.

## How the tests are run

Each item names the tests its change affects, in tests, one unit to an entry: the test classes that test what the
item changes, the tests it writes among them - or, for a runner that selects tests by name, those tests' names. Write
test classes, not the classes the item changes: a runner selects tests, and which tests reach a class is not known
from the class. The engine tests each item on its units alone - with any its builder adds as it works - and the gate
of each round on the units of every item built in the run, or on every test where the run's input finalTests says all
(it is `{input.finalTests}`). An item whose change affects no test leaves tests empty and says in noTests why: what
makes its work a document, a comment or a file no test reads. It is tested on none, and its review judges the reason.
The legs this run tests on, each with its test runner as the repository's settings give it, the option a filter is
given through, and what the leg leaves out:

{orient.tests}

Each unit is given to the runner after that option exactly as you write it - the units joined into one argument where
the leg's settings join them, each after an option of its own where they do not: write each in what that option takes
for that runner. The units are given to every leg the item is tested on, so each has to mean the same tests to each
leg. A unit may name only tests that are there or that the item writes. One that a leg's own exclusions leave nothing
of is refused before anything is built, and a person is asked.

## What the recovery is fixing

Every issue that was there before the run that the run has taken, each with its id, what became of it, its title and
where; empty where none is:

{run.preExisting}

{text.fixes}

## How recommendations are made in this repository

{recommendations}

## The universe this repository declares

{text.closure}

Where the file above says a heading is empty, the repository has not declared its universe yet: take the values of each
axis from what the repository builds and tests - its kinds, targets, configurations, formats, entry points, tiers and
forms, both directions.

## Close the class, at its one home

For every defect the plan fixes and every behaviour it changes, write the class it belongs to, and work it as one item:

1. Write the invariant in one sentence: X is decided by Y for every Z.
2. Find the one home that should decide it - or say that no place does today, and creating the home is the fix.
3. Find every site that decides it today, by searching the data - the kind, the key, the diagnostic, the verb, the
   literal - across every tier, format, target and file kind, never the file the defect was seen in alone. The
   graph's sites first: it joins every place that decides the data - what defines, calls, uses, makes, references,
   inherits, implements or overrides it, every literal that holds it and every key it names - and its coverage lines
   say what it searched by text alone, for nothing is assumed of a language with no extractor. Its find, node and
   related beside it for the names and for what reads and calls them, its impact for everything a change of a node
   reaches, its around and its outline - of a file or a directory - to know where a node stands before reading any
   file whole, and text search for what the graph does not see, each such text search said in notCovered.
4. Record each search: what was searched, how, the query as it was made, and how many places it found.
5. Write the universe: an answer for every axis the closure text lists - your answer's schema refuses a universe that
   leaves one out - and for each further one it names. An axis the decision ranges over gives its values whole, from
   a search of the tree for every cell of its kind - for the legs and the references, every file of every service that
   builds, tests, lints or checks the repository, never only the CI it names first - recorded as a class's searches
   are, and where the repository declares them; one it does not range over says why, as something an auditor can check
   in the code. Its cells are the product of the counts of the values of the axes that apply. An axis written from the
   cells you happened on leaves one out, and an auditor holds the plan back for it, as it does an axis said not to apply
   that the code shows the decision ranges over.
6. Where more than one site decides, the action is consolidate: the home decides, every other site reads it, and the
   item owns every path that changes. Patching some of a class's sites is never done; a class with one site names the
   home itself, and its searches prove it is the only one. The site at the home's path and symbol is marked home, and
   no other is: every other site reads the home or is deleted.

The work of a class goes to one item, which owns every site that changes, so items are split by classes before they are
split by files. An item that decides nothing - a document, a rename the tools make, a dependency bump - gives no class
and says why in noClass.

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
5. Design the work, then split it into items by sets of files, as many as it needs, as your instructions say. Size each item to
   one builder's cycle: as much work as one agent builds whole in a single sitting, with its tests - never more than
   that, and never a sliver of one; an item too small to fill a builder is merged with the work beside it. Items are
   built at the same time: none may need another's result. Leave nothing inside an item that you already know only a
   person can give: ask it now, or, where you may not, leave it out and name it in the summary. A builder that finds
   part of its item cannot be done says so, and a person is asked before anything of the item is folded.
6. For each item write: instructions a builder with none of this conversation can follow; its definition of done -
   each behaviour, the test that pins it, and what is true of the tree when the builder reports - by the rule for a
   definition of done below, the run's base commit being `{orient.startCommit}`; the paths it owns and the paths it
   must not touch; its tests, or why it affects none; what it reuses, by path and for what; what it creates, with why
   nothing that exists serves and where you searched; and what it replaces because the request asked, by path, with
   what becomes of the old one.

## A definition of done

{text.done}

## What to return

A summary of the plan, naming each part of the request no item covers, with the reason; the plan's definition of
done - what holds when the whole plan is built, as statements a check can answer one by one, by the same rule, which
the run's final audit holds every round against; whether it was built on a plan a person gave or made here; each departure from a
given plan, with its reason; the items, each with its classes or its noClass; and under `scope` an empty list, since
nothing is being updated.

A machine checks the items before anyone builds them: every name valid, no file under two items, forbidden paths kept,
every path an item reuses or replaces there in the tree, every item saying what it reuses, creates or replaces, each
home and site a path inside the tree, each class's home among the item's own paths and there in the tree, a site marked
home only where it is the home, every other site the item's too, more than one site consolidated at the home, each
class searched by data its queries name again, each axis of a universe answered one way or the other - values with where
they are declared, or no value with why - each universe counting the cells its axes that apply make, one class id used
once, and one home belonging to one item. It reads your answer as you give it: what it finds wrong is told you
here, in this conversation, to put right in the answer you give again. An independent auditor then checks
the plan against the code, searching for itself what you say does not exist - the classes above all. Then
a person approves it, unless they approved it beforehand in what they gave this run.

## Issues that were there before

{text.issuesBefore}
