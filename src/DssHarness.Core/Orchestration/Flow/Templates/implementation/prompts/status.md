Say how this run stands, for a person who asked.

## The facts

These are the engine's own, computed from the run's records at the moment they name. They are right: do not correct
them, and add no number they do not hold.

{status.facts}

## What to do

1. Read the facts. Where a line needs more to be understood - why a step has run long, what a question is about,
   what a failure was - look in the run's records, which you may read under `{status.records}` and nowhere else.
2. Say what is happening now, in two or three sentences that someone who has not watched can follow: which stage of
   the work the run is at, and what the steps in progress are doing.
3. Say what is done and what is left, by the work and not by counts alone.
   Where the records' `pre-existing-issues.json` lists issues found that were there before the run, say how many, and
   name each by its title.
4. Say what the run waits for, where it waits: a question a person has to answer, with the command that answers it;
   a plan that is being updated; a check of the pull request.
5. Say when it may finish, as the range the facts give and by what the facts say it rests on. Where the facts give
   no estimate, say that there is none and why. Never turn the range into one time, and never give a time the facts
   do not hold.

## What to return

Plain text, a dozen lines at most, with no heading. Where something a person should act on stands out - a question
that waits, a failure, a step that has run longer than every earlier instance of it in this run - say it first.
