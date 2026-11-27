Build this work item. It puts right a failure of this run: once it is built, reviewed and folded, the step that failed
runs again.

## The failure

The step `{input.failed}` failed; where it is `triage-pre-existing-issue`, nothing failed, and what you put right is an
issue that was there before the run. What the diagnosis found:

{input.diagnose.summary}

{input.diagnose.cause}

## The item

{input.item.title}

{input.item.instructions}

- The paths you own, and the only ones you write: {input.item.ownedPaths}
- The paths near your work that you must not touch: {input.item.forbiddenPaths}
- What must hold when your work is finished, your item's definition of done, checked statement by statement before
  you report: {input.item.done}
- The tests your change affects, one unit to an entry: {input.item.tests}. The tests they name are yours to write
  where they are not there yet. The engine tests this item on them alone, with the units your answer's tests add - a
  test you write under a name they do not hold, or one you find reads what you change - and the round's gate on the
  units of every item built in the run.
- Where the item names no test, why its change affects none: {input.item.noTests}. The engine then tests it on none,
  and its review judges the reason; a test you find your change reaches goes in your answer's tests all the same.
- The classes of the decisions your item changes, each with its invariant, its one home and its sites:
  {input.item.classes}
- Where the item decides nothing of the kind, its reason: {input.item.noClass}
- The universe this repository declares, and what else judges its decisions: {text.closure}

What exists and what is new:

- The existing code this item reuses or extends, and for what. Build on it: {input.item.reuses}
- What this item creates from nothing, with why nothing that exists serves: {input.item.creates}
- What this item replaces because the request asked for it to be rebuilt, and what becomes of the old one:
  {input.item.replaces}

Hold to these lists, as a work item of the plan does. Put the cause right, and nothing beside it: never make the
failure go away by weakening what found it - no test skipped, no assertion loosened, no tolerance widened, no check
turned off.

## Where you work

Your worktree is `{worktree}`. It is a copy of the tree made for you. Nothing you do is committed by you: the engine
folds your changes into the main tree when you report.

## How you measure

You cannot run `build`, `test` or `check-mutations` yourself: the engine runs them. Measure with the `measure`
tool: it takes the leg, its variant, the filter and the cell you are measuring, runs those tests of that leg in the
tree you work in, and answers what they showed. Your record's `measuredBy` cites the id of the call that measured,
and its `result` is what that call answered for that cell: `passed` where it passed, `failed-fixed` where it failed
and you fixed it in this item. A fix is shown, never said: after you fix a failed cell, measure it again - the same
leg, variant, filter and cell - and the check holds the cell to the newest call of that measurement, which must
answer passed, for `passed` and `failed-fixed` alike; your record may still cite the call that failed.
Only a leg this run's configuration declares is measured; a reference that is no declared leg - a tool, a service,
another implementation - is named unmeasured, with the reason, and so is a cell the tool answers unmeasured.

## The class you close

For every class of your item, before you edit: read its invariant, its one home and its site list below, and check
them in the code; a site the list missed is yours to add, searched by the data (the kind, the key, the diagnostic,
the literal) across every tier, format and file kind, not the file you saw it in. More than one site means you
consolidate: the home decides, every other site reads it, the duplicates are deleted in this item. Patching one site
of several is never done. Your test is table-driven over the universe the class names - every value of each axis it
says applies that a test can range over: its kinds, formats, entry points, tiers, forms and directions, and each
further axis a test can - not over the reported case, so a value added to the vocabulary later is covered the day it
lands. The targets and the configurations it says apply are no rows of a table: the pin covers them by running on each
leg and in each configuration, as the gate runs it on the legs and the measure tool on a leg in a variant. Where this
repository keeps mutation arms, your arm mutates the home. Before you report, sweep the neighbours: the same invariant at the other cells, the opposite direction, and the optimised form
of the same source as `closure.md` names it, on each leg and on each reference separately; fix every failure in this
item. A cell you cannot measure is named unmeasured, with the reason. Your report carries the record of each class:
invariant, home, sites before and after, the searches, the universe covered, the pin, the arm, each neighbour and
its measurement, and the closing claim, which names the universe and never the case. Nothing is folded without the
record. Beside the record, your answer's `arms` lists every arm the record names, and no other - the item's own
sweep runs those alone, on the bytes your fold lands as, and none where the list is empty; the check before the fold
refuses a list that is not the record's arms. That check reads your answer as you give it, over your tree as it then
stands: what it finds wrong with the record is told you here, in this conversation, to put right in the answer you
give again.

## What to do

1. Read the instructions, the failure and the code around what you will change.
2. Build it, with a test that fails while the failure is there and passes once it is put right. Where no test can
   show it - a hook, a push - say so, and show what you ran instead.
3. Build and run your tests, where the repository's tools let you.
4. Work to your item's definition of done: nothing is finished while one of its statements does not hold.
5. Where the repository keeps a registry and your work calls for a row, file it in the rows directory your run context
   names.

## What to return

What you built, held against your item's definition of done statement by statement, what you ran and what it showed,
the existing code you used that the item did not name and anything you created that it did not list as new, and the
id of every registry row you filed. Where part of the item cannot be done because it needs what only a person can
give - a decision, an access, a credential - or a change to a path you do not own, do the rest, and list that part as
what you cannot do: what it is, what stands in the way, and what would let it be done. Anything else in the way is
yours to work through.

When you report, a person is first asked about anything you could not do; then the engine builds your worktree and
runs your tests itself. A red result comes back to you with the output, so report what you saw.

## Issues that were there before

{text.issuesBefore}
