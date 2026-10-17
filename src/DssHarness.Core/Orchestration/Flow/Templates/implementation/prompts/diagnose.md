A step of this run failed, and nothing in the flow took the failure. Find out why, and say what puts it right.

## What failed

The step `{source.step}` ended with exit code {source.code}:

{source.error}

What it found, where it is a check - each red leg, check or finding with its last lines and where its whole output
is:

{source.red}

Where the step failed because a question about it was answered so, the question's kind: {source.question}

Where `{source.step}` is `triage-pre-existing-issue`, nothing failed: you are given an issue that was there before the
run, proven on the base commit `{orient.startCommit}`, to fix at its root. What it is: {source.title}. {source.description}
Where: {source.where}. Its proof: {source.proof}. Then the first statement of each item's definition of done is that
the proof no longer shows the issue, in place of the step passing.

This is the recovery's first visit for this step. Should what you decide not put it right - the step fails again, or
the work you set out fails - you are asked again, with what happened, and you go on from here.

## The run

What it is for: {intake.triage.title}

{intake.triage.summary}

The plan it builds, where it has one yet: {planning.plan.latest.summary}

How the tests are run here - the legs this run tests on, each with its test runner, the option a filter is given
through, and what the leg leaves out:

{orient.tests}

What the run has checked so far, and every red result it knows of:

{run.ran}

{run.red}

## What to do

1. Read the failure, and the output and logs it names, and find the first thing that went wrong.
2. Find the cause in the code and the configuration, and reproduce it where the repository's tools let you.
3. Decide the course by the cause:
   - **fix**: work items that put it right - at most four, each sized to one builder's cycle, with the paths it owns,
     the test filter that shows the failure is gone, and its definition of done, of which the first statement is that
     `{source.step}` would now pass. Reuse what the repository has; change only what the cause needs.
   - **retry**: nothing to change, and why running the step again will pass where it did not.
   - **person**: what only a person can give, and the choices, each with what it leads to. Recommend none of them: the
     run's advisor does, by the repository's bar.
4. Never weaken what found the failure to make it pass.

## What to return

The course; a summary a person who was not there can follow; the cause; the evidence; the work items where the course
is fix, empty otherwise; the choices where the course is person, empty otherwise.
