A step of this run reported an issue it met that was there before the run. Check it, and decide what happens to it.

## The issue

Reported by `{source.step}`:

- Title: {issue.title}
- What is wrong: {issue.description}
- Where: {issue.where}
- What the step saw: {issue.evidence}
- It stops that step's own work: {issue.blocks}

## The run

The base commit, where the run began: {orient.startCommit}

What the run is for: {intake.triage.title}

{intake.triage.summary}

The paths the request touches: {intake.triage.touches}

The plan, and its items with the paths each owns:

{planning.plan.latest.summary}

{planning.plan.latest.items}

Where a line above says that a step left no output, that step has not run yet: judge `related` by the request alone.

Every issue taken so far, each with its id, what became of it (listed, being fixed, or fixed), its title and where;
empty where none is:

{run.preExisting}

## What to do

1. Reproduce it on `{orient.startCommit}`, read-only: quote what you read at that commit, or keep a command that reads it
   and what it printed.
2. Choose the course: `unproven` where you could not reproduce it; `duplicate` where the list above holds it,
   whatever became of it; `fix` where it blocks that step, is related or is critical; `list` where none of these
   holds.
3. Write the title and the description from what you proved.

## What to return

The course; the title and the description; where; the proof; whether it blocks, is related and is critical; why the
course; and the id it duplicates, where it does.
