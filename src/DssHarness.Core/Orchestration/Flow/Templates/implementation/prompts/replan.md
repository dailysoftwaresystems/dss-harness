Your plan did not pass. Revise it, and return the whole plan again in the same form.

## What the machine check said

Passed: {previous.check.ok}. Problems: {previous.check.problems}

## What the auditor said

Go: {previous.plan-audit.go}. Findings:

{previous.plan-audit.findings}

What the auditor's closure part found, each class with the sites it searched out that the plan lacks and the cells of
its universe it leaves out - the values an axis that applies lacks, and each axis said not to apply that the decision
ranges over:

{previous.plan-audit.closure}

## What the person said

Answer: {previous.approval.answer}. What to change:

{previous.approval.text}

Where a line says that a step left no output, that step did not run in the last round and has nothing to say.

## What this run owes its next round

The issues this run owes its next round, each with its id and its size - the files its fix touches, what must be
built, and what must be measured first:

{run.nextRound}

An empty list means none is owed. Each issue listed is a work item of the plan, which its first round builds before
the rest, naming the issue it settles in `settles` by its id exactly: keep the one the plan has, or make one, with its
size and a class, where it has none. The round's check refuses a round whose items leave one unsettled.

## What to do

Act on every problem, every finding and everything the person asked for; where the person's words and a finding
disagree, the person's words hold. Where you think a finding is wrong, keep that point and say in the summary which
finding and what you read that shows it wrong. Change what has to change and leave the rest, so that what already
passed still passes. Where the auditor's closure part names a site your plan lacks, search the data for it yourself
before you add it, and extend the class's site list, its searches and its universe to hold what you find. Where it
names a cell of a universe your plan leaves out, the axis was not searched whole: search the tree for every cell of that
axis, as a class's searches are made, and hold every one you find, never only the one it named - an audit that finds
another holds the plan back again; an axis it shows your plan said does not apply is answered with its values,
searched whole the same way. Where it names a statement of the definition of done the base commit already fails,
narrow the statement to what the work changes. An item that
changes a decision still gives the class, and one that decides none still says why in noClass.

A finding that the plan builds what the repository already has, or writes a rule a second time, is settled by
planning the extension of what exists, not by a better reason for the new thing - unless the code it names does not
do what the finding says. Whatever this revision adds, search for first, as before, and keep each item's lists of
what it reuses, creates and replaces true of the plan as it now is.

## What the recovery is fixing

Every issue that was there before the run that the run has taken, each with its id, what became of it (listed, being
fixed or fixed, alone or with the issue whose root cause it shares), its title and where; empty where none is:

{run.preExisting}

{text.fixes}

## Where a plan update is under way

{update.cause}

Where the lines above say that none is under way, skip this section and keep `scope` empty. Otherwise keep each
piece of `scope` true of the plan you now return. The work of this run, with what to call each piece by:

{update.work}

## How recommendations are made in this repository

Where this revision leaves something open that you may still ask the person, each question comes with its choices and
the one you recommend, by this bar as before:

{recommendations}
