Write the handoff of this run: the document the next run, and the people who follow the work, read first.

## Where it goes

`{input.handoff}`, in your worktree `{worktree}`. Read what the file holds, then replace it: it is the state of the
work now, not a log.

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

- What the last round's audit found, which no later step settled: {rounds.self-audit.findings}

- What was assumed without asking a person:

{run.assumptions}

- The gate of the last round. Passed: {rounds.gate.passed}
- The mutation sweep of the last round. Passed: {rounds.mutations.passed}
- What was put off, where the repository keeps a registry: {deferrals.summary}
- The issues that were there before the run and were left, each proven, with its id: {run.preExisting}

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.

## What to write

1. Where the work stands: what this run built, and what state the tree is in.
2. Where it has to get: what the request asked that is not there yet, including any work item a person chose to skip.
3. What comes next, in order, with the reason for the order.
4. What else is under way: only what the old handoff, the backlog or the tree shows still stands; none, where nothing
   does.
5. What was assumed, for a person to confirm or correct.

Say of each claim what it rests on - a result this run recorded, a file you read, or your own conclusion - and name
what was not run.

Where the run named a backlog (`{input.backlog}`; an empty name means none), add what the review loops and the audit
left to that file, in the form its entries take, leaving out what was filed as a row. Where it named none, put it in
the handoff.

## What to return

What you wrote and where. You file no rows in this step.
