Write the handoff of this run: the document the next run, and the people who follow the work, read first.

## Where it goes

`{input.handoff}`, in your worktree `{worktree}`. Replace what the file holds: it is the state of the work now, not a
log.

## What the run recorded

- The work: {intake.triage.title}
- What it was: {intake.triage.summary}
- The plan: {planning.plan.latest.summary}
- What was checked, and how each check ended:

{run.ran}

- What was not checked:

{run.notRun}

- What is known to be red:

{run.red}

- What the review loops and the rounds' audit left:

{run.leftover}

- What was assumed without asking a person:

{run.assumptions}

- The gate of the last round. Passed: {rounds.gate.passed}
- The mutation sweep of the last round. Passed: {rounds.mutations.passed}
- What was put off: {deferrals.summary}

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.

## What to write

1. Where the work stands: what this run built, and what state the tree is in.
2. Where it has to get: what the request asked that is not there yet, a work item a person chose to leave out among
   it.
3. What comes next, in order, with the reason for the order.
4. What else is under way that the next run must know of.
5. What was assumed, for a person to confirm or correct.

Say of each claim what it rests on - a result this run recorded, a file you read, or your own conclusion - and name
what was not run.

Where the run named a backlog (`{input.backlog}`; an empty name means none), add what the review loops and the audit
left to that file, each as an entry someone can pick up. Where it named none, put it in the handoff.

## What to return

What you wrote and where. You file no rows in this step.
