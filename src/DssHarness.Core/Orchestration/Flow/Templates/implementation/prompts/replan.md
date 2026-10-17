Your plan did not pass. Revise it, and return the whole plan again in the same form.

## What the machine check said

Passed: {previous.check.ok}. Problems: {previous.check.problems}

## What the auditor said

Go: {previous.plan-audit.go}. Findings:

{previous.plan-audit.findings}

## What the person said

Answer: {previous.approval.answer}. What to change:

{previous.approval.text}

Where a line says that a step left no output, that step did not run in the last round and has nothing to say.

## What to do

Act on every problem, every finding and everything the person asked for; where the person's words and a finding
disagree, the person's words hold. Where you think a finding is wrong, keep that point and say in the summary which
finding and what you read that shows it wrong. Change what has to change and leave the rest, so that what already
passed still passes.

A finding that the plan builds what the repository already has, or writes a rule a second time, is settled by
planning the extension of what exists, not by a better reason for the new thing - unless the code it names does not
do what the finding says. Whatever this revision adds, search for first, as before, and keep each item's lists of
what it reuses, creates and replaces true of the plan as it now is.

## Where a plan update is under way

{update.cause}

Where the lines above say that none is under way, skip this section and keep `scope` empty. Otherwise keep each
piece of `scope` true of the plan you now return. The work of this run, with what to call each piece by:

{update.work}

## How recommendations are made in this repository

Where this revision leaves something open that you may still ask the person, each question comes with its choices and
the one you recommend, by this bar as before:

{recommendations}
