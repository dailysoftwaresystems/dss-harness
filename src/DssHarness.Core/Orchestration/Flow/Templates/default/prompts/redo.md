Do this work item again: a person changed the plan while the run worked, and the change reaches it.

## What changed

{piece.cause}

What the change means for this item: {piece.changed}

- What of the work done so far stays: {piece.keep}
- What is taken back: {piece.undo}
- What is done in its place: {piece.instead}

## The item as it now is

{item.title}

{item.instructions}

- The paths you own, and the only ones you write: {item.ownedPaths}
- The paths near your work that you must not touch: {item.forbiddenPaths}
- The filter that selects your tests when the engine runs them: `{item.testFilter}`; empty, every test runs
- The existing code this item reuses or extends, and for what: {item.reuses}
- What this item creates from nothing, with why nothing that exists serves: {item.creates}
- What this item replaces because the request asked for it to be rebuilt, and what becomes of the old one:
  {item.replaces}

The plan this item is part of, as it now is: {planning.plan.latest.summary}

## What the earlier work left

{piece.before}

That account is the engine's, from its records. It says where the earlier work is: in the worktree you are in, as
the agent before you left it, or in the tree you were given, in the commits it names. Read what is there before you
change anything, and trust the files over the account where they differ.

## Where you work

Your worktree is `{worktree}`. Nothing you do is committed by you: the engine folds your changes into the main tree
when you report.

## What to do

1. Read what the earlier work built, in the files, and set it beside the item as it now is.
2. Keep what the change leaves standing. Do not build again what is already right.
3. Take back what the change takes back: the code, its tests, and anything that exists only for it. Leave nothing
   of it standing beside what takes its place.
4. Build what the item now asks for, with a test for each behaviour that fails when the behaviour is broken, and
   change the tests of what you kept where the change makes them say something else.
5. Build and run your tests, where the repository's tools let you.
6. Where the repository keeps a registry: a row the earlier work filed for something that is now gone is settled by
   the registry's own rule, not deleted; file a row where the new work calls for one, in the rows directory your
   run context names.

## What to return

What you kept, what you took back and what you built, by file; what you ran and what it showed; the existing code
you used that the item did not name and anything you created that it did not list as new; the id of every registry
row you filed; and, as what you cannot do, any part of the item that cannot be done, with what stands in the way and
what would let it be done: a person is asked about it before your work is tested or folded.

When you report, the engine builds your worktree and runs your tests itself. A red result comes back to you with the
output, so report what you saw.
