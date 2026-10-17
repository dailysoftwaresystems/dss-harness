Choose what this run works on. No request was given, so the choice is yours.

## What you have

- The handoff the last run left: `{input.handoff}`, a file in the repository. It may not be there.
- The backlog: `{input.backlog}`, a file in the repository. An empty name means the repository keeps none.
- A plan a person gave this run: `{input.plan}`. An empty name means none. Where one is given, that plan is the work:
  do not choose something else, and name what stands in its way.
- The repository's registry, where it keeps one: read its open rows.
- CI on `{orient.baseBranch}`, the branch this run's pull request goes to, as this run read it. Passed: {ci.passed}.
  The matrix ran: {ci.matrixRan}. Red legs:

{ci.red}

- The baseline: the repository's own tests, run on the tree as this run found it. Passed: {baseline.passed}. Red legs:

{baseline.red}

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.

## What to do

1. Read the handoff, and check what it says against the tree and the log before you rely on it.
2. Read the backlog and the open rows.
3. Weigh CI and the baseline. A red CI leg is the priority, and its repair is the work. With a plan given, a red leg or
   a red baseline is not yours to put first: name it as a stop, for a person to say whether the plan goes ahead
   beside it.
4. Choose one piece of work, and find what stands in its way. Where nothing is red and neither the handoff nor the
   backlog names open work - or neither is there - there is nothing to choose from: choose nothing, and write one
   stop that asks the person what this run should do, with the choices you can see and the one you recommend. Say in
   the request that the work waits for that answer.
5. Write each stop as a question to the person who has to lift it: the problem, what you checked, what it means, the
   choices, the one you recommend, and why.

## How recommendations are made in this repository

{recommendations}

## What to return

The title; the work, written as a request a person could have typed; why it comes first, with what you checked; the
blockers, the most pressing first; and the stops, each with its choices and the one you recommend by the bar above.
