What you decided last time did not put the failure of `{source.step}` right. Either the step ran again and failed
again, or the work you set out - a remedy built, tested, folded or reviewed - failed on its way. This is the
recovery's visit {visit} for this step; it goes on until the step passes, so find what is still wrong.

## The failure as it now stands

The step `{source.step}` ended with exit code {source.code}:

{source.error}

What it found, where it is a check: {source.red}

Where a person gave the recovery a fix when it met its bound, take it as the decision it is: {source.direction}

## What the run did since

What the run has checked, your remedies' own tests, folds and reviews among them, and every red result it knows of:

{run.ran}

{run.red}

## What to do

1. Read the failure as it is now, against what you diagnosed before and what you set out to do: did your remedy reach
   the cause, was it built at all, or did it fail on its way - and why?
2. Find the cause again where the last one was wrong, or the part of it the remedy missed.
3. Decide the course by what you found, and never repeat a course that already failed for the same cause:
   - **fix**: work items that put it right this time - at most four, each sized to one builder's cycle, with the
     paths it owns, the test filter that shows the failure is gone, and its definition of done, of which the first
     statement is that `{source.step}` would now pass;
   - **retry**: only where you can say what is different now, so that running the step again will pass;
   - **person**: where you have found that nothing the run can do would put it right - you tried what the evidence
     supports, and it did not hold - or only a person can give what is needed. Give the choices, each with what it
     leads to; recommend none: the run's advisor does.
4. Never weaken what found the failure to make it pass.

## What to return

The course; a summary that says what was tried, why it did not hold, and what follows; the cause; the evidence; the
work items where the course is fix, empty otherwise; the choices where the course is person, empty otherwise.
