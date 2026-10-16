The pull request of this run is open, and its checks are red. Repair what they show.

## The pull request

{pr.url}, for: {intake.triage.title}

{intake.triage.summary}

## The red checks, on commit `{checks.commit}`

{checks.red}

Each entry names the check, the job and the step that failed, the last lines the job printed, and the file that holds
its whole log where there is one.

## Where you work

Your worktree is `{worktree}`, a copy of the tree as the pull request has it.

## How the tests are run here

The legs this run tests on, each with its test runner, the option a filter is given through, and what the leg leaves
out:

{orient.tests}

The filter you return is given to each leg's runner after that option, exactly as you write it. Empty is no filter:
every test runs.

## What to do

1. Read the output of each red check and find the first thing that went wrong.
2. Find the cause, and reproduce it here where you can.
3. Put the cause right, with a test that would have caught it. Never weaken a check to make it pass.
4. Where the cause is not in the code - a runner that failed, a service that was down, a test that fails at random, a
   check that waits for a person - change nothing and say so. You cannot run a check again.
5. Where the repair needs what you cannot do, do the rest and list that as what you cannot do: what it is, what
   stands in the way, and what would let it be done.

## What to return

What was red and why; whether you changed anything; where you did not, the cause and what would settle it; the filter
that selects the tests that prove the repair; what you ran and what it showed; what you cannot do, if anything; and
the id of every registry row you filed.

Where you changed nothing, or list something you cannot do, a person is asked first. When you report a change, the
engine tests it, folds it, commits it and pushes it, and waits for the checks again.
