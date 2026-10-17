Build this work item.

## The item

{item.title}

{item.instructions}

- The paths you own, and the only ones you write: {item.ownedPaths}
- The paths near your work that you must not touch: {item.forbiddenPaths}
- What must hold when your work is finished, your item's definition of done, checked statement by statement before
  you report: {item.done}
- The filter that selects your tests when the engine runs them: `{item.testFilter}`. The tests it names are yours to
  write where they are not there yet; where it is empty, the engine runs every test.

What exists and what is new:

- The existing code this item reuses or extends, and for what. Build on it: {item.reuses}
- What this item creates from nothing, with why nothing that exists serves: {item.creates}
- What this item replaces because the request asked for it to be rebuilt, and what becomes of the old one:
  {item.replaces}

Hold to these lists. Where you find existing code the plan missed that does what you were about to write, use it and
say so in your report. Create nothing the second list does not name without looking for it first, and leave nothing
you replace standing beside its replacement.

The plan this item is part of, for context: {planning.plan.latest.summary}

## Where you work

Your worktree is `{worktree}`. It is a copy of the tree made for you; other items are being built in other copies at
the same time. Nothing you do is committed by you: the engine folds your changes into the main tree when you report.

## How the engine runs your tests

The legs it tests on, each with its test runner, the option your filter is given through, and what the leg leaves out:

{orient.tests}

## What to do

1. Read the instructions and the code around what you will change.
2. Build it, with a test for each behaviour that fails when the behaviour is broken.
3. Build and run your tests as the engine runs them, above. Where you cannot, say why in what you ran.
4. Work to your item's definition of done: nothing is finished while one of its statements does not hold, and part
   of an item is never reported as the item.
5. This repository keeps an anchor registry of deferred work: {orient.anchors}. Where true and you leave something
   for later, file one row for it in the rows directory your run context names, and cite its id where the work
   belongs. Where false, file no row.

## What to return

Fill each member of the answer as its description says. Where part of the item cannot be done, do the rest and list
that part as what you cannot do.

When you report, a person is first asked about anything you could not do; then the engine builds your worktree and
runs your tests itself. A red result comes back to you with the output, so report what you saw.
