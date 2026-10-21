Build this work item. It puts right a failure of this run: once it is built, reviewed and folded, the step that failed
runs again.

## The failure

The step `{source.step}` failed; where it is `triage-pre-existing-issue`, nothing failed, and what you put right is an
issue that was there before the run. What the diagnosis found:

{diagnose.latest.summary}

{diagnose.latest.cause}

## The item

{item.title}

{item.instructions}

- The paths you own, and the only ones you write: {item.ownedPaths}
- The paths near your work that you must not touch: {item.forbiddenPaths}
- What must hold when your work is finished, your item's definition of done, checked statement by statement before
  you report: {item.done}
- The filter that names the tests your change affects: `{item.testFilter}`. The tests it names are yours to write
  where they are not there yet. The engine runs only those for this item where the run's input affectedTests is on
  (it is `{input.affectedTests}`), and every test where it is off or the filter is empty; every test runs once every
  item of the round is built, either way.

What exists and what is new:

- The existing code this item reuses or extends, and for what. Build on it: {item.reuses}
- What this item creates from nothing, with why nothing that exists serves: {item.creates}
- What this item replaces because the request asked for it to be rebuilt, and what becomes of the old one:
  {item.replaces}

Hold to these lists, as a work item of the plan does. Put the cause right, and nothing beside it: never make the
failure go away by weakening what found it - no test skipped, no assertion loosened, no tolerance widened, no check
turned off.

## Where you work

Your worktree is `{worktree}`. It is a copy of the tree made for you. Nothing you do is committed by you: the engine
folds your changes into the main tree when you report.

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
