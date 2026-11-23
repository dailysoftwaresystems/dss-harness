The engine built your work and ran its tests, and the result is red. Repair it.

## The red result

{source.red}

The whole logs are under `{source.runDirectory}`. This is visit {visit} of this repair.

Each entry names a leg, the phase that did not pass - the build, or the tests - and what that phase printed,
digested: in `digest`, the first error whole - its stack rendered to the frames inside the tree, its `log` naming the
lines of the log it was read from - the next distinct errors to four as one line each, every test failure of the four
with its stack, and the rest counted by code and by file; the `tail` then holds only the lines no parser held.

How the engine runs the tests, leg by leg: {input.orient.tests}

Every red result the run knows of, those from before its changes among them:

{run.red}

## The class you are closing

The classes of the decisions this item changes, each with its invariant, its one home and its sites:

{input.item.classes}

The record of each class so far, as the last report gave it:

{implement.latest.closure}

Act by them: a site the list missed is yours to add and to close with the rest, and nothing of the class is done
while one of its sites is left.

## What to do

1. Start from the digest's `first`: it is the first thing that went wrong, in the order the tool met it. The log
   lines its `log` names are one read away, under `{source.runDirectory}`, where the cause needs more than the digest
   says.
2. Find the cause in your change. Put the cause right; do not make the test pass by weakening it.
3. Where a leg says it found no test to run, a unit your task gave or your answer added names tests that are not
   there: where your work was to write them, write them; a unit your answer added that names none, leave out of your
   answer's tests.
4. Build and run the tests again as the engine runs them. Where you cannot, say why in what you ran.

Keep to the paths your task allows. A red result that was there before this run - a test red before its changes, a
missing tool - is not yours to fix unless your item was set to fix it: report it in `preExisting`, quoting the line above or of the output that shows it
red before the run's changes, with `blocks` true where it stops your work. Without such a line, it is yours.

## What to return

The same report as before, brought up to date, every list in full: what you changed and why, and what you ran and what
it showed.

When you report, the engine runs the tests again.
