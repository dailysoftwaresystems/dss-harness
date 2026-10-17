Record what this run put off, in the repository's registry.

## What the run recorded

What the review loops and the rounds' audit left when they ended:

{run.leftover}

What is known to be red:

{run.red}

The gate of the last round, the whole of the repository's tests on the main tree. Passed: {rounds.gate.passed}. Red
legs: {rounds.gate.red}

The plan of this run: {planning.plan.latest.summary}

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.

## What to do

1. For each thing above that still holds when you open the place it names, decide whether it is a deferral by your
   instructions' test: a named thing in the way, the better course and not the easier one, a reason that stands.
2. File a row for each deferral in the rows directory your run context names, in the form the rows already there take.
   Each row says what is put off, what is in the way and what would lift it.
3. What fails the test is not a deferral. Do not file it; name it in your summary as unfinished.

Your worktree is `{worktree}`. Write nothing but rows.

## What to return

What you filed and what you left unfiled and why; and the id of every row you filed.
