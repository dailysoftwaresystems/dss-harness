Build this work item.

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

Hold to these lists. Where you find existing code the plan missed that does what you were about to write, use it and
say so in your report. Create nothing the second list does not name without looking for it first, and leave nothing
you replace standing beside its replacement.

The plan this item is part of, for context: {input.planSummary}

## Where you work

Your worktree is `{worktree}`. It is a copy of the tree made for you; other items are being built in other copies at
the same time. Nothing you do is committed by you: the engine folds your changes into the main tree when you report.

## How the engine runs your tests

The legs it tests on, each with its test runner, the option your units are given through, and what the leg leaves
out:

{input.orient.tests}

## The class you close

For every class of your item, before you edit: read its invariant, its one home and its site list below, and check
them in the code; a site the list missed is yours to add, searched by the data (the kind, the key, the diagnostic,
the literal) across every tier, format and file kind, not the file you saw it in. More than one site means you
consolidate: the home decides, every other site reads it, the duplicates are deleted in this item. Patching one site
of several is never done. Your test is table-driven over the universe the class names - each axis's values, both
directions, every form - not over the reported case, so a value added to the vocabulary later is covered the day it
lands; where this repository keeps mutation arms, your arm mutates the home. Before you report, sweep the
neighbours: the same invariant at the other cells, the opposite direction, and the optimised form of the same
source as `closure.md` names it, on each leg and on each reference separately; fix every failure in this item. A
cell you cannot measure is named unmeasured, with the reason. Your report carries the record of each class:
invariant, home, sites before and after, the searches, the universe covered, the pin, the arm, each neighbour and
its measurement, and the closing claim, which names the universe and never the case. Nothing is folded without
the record.

## How you measure

You cannot run `build`, `test` or `check-mutations` yourself: the engine runs them. Measure with the `measure`
tool: it takes the leg, its variant, the filter and the cell you are measuring, runs those tests of that leg in the
tree you work in, and answers what they showed. Your record's `measuredBy` cites the id of the call that measured.
A reference that is a leg or a tool the repository's configuration runs is measured the same way; one that is none
of those is named unmeasured, with the reason.

## What to do

1. Read the instructions and the code around what you will change.
2. Build it, with a test for each behaviour that fails when the behaviour is broken.
3. Build and run your tests as the engine runs them, above. Where you cannot, say why in what you ran.
4. Work to your item's definition of done: nothing is finished while one of its statements does not hold, and part
   of an item is never reported as the item.
5. This repository keeps an anchor registry of deferred work: {input.orient.anchors}. Where true and you leave something
   for later, file one row for it in the rows directory your run context names, and cite its id where the work
   belongs. Where false, file no row.

## What to return

Fill each member of the answer as its description says. Where part of the item cannot be done, do the rest and list
that part as what you cannot do.

When you report, a person is first asked about anything you could not do; then the engine builds your worktree and
runs your tests itself. A red result comes back to you with the output, so report what you saw.
