Take this work item back: a person changed the plan while the run worked, and the item is withdrawn.

## What changed

{piece.cause}

What the change means for this item: {piece.changed}

- What is taken back: {piece.undo}
- What stays, though this item made it: {piece.keep}
- What is done in its place, if anything: {piece.instead}

## The item that is withdrawn

{item.title}

{item.instructions}

- The paths you may write, and the only ones: {item.ownedPaths}
- The paths you must not touch: {item.forbiddenPaths}
- What must hold once it is taken back, checked statement by statement before you report: {item.done}
- The filter that selects the tests the engine runs when you report: `{item.testFilter}`; empty, every test runs

## What the item built

{piece.before}

That account is the engine's, from its records: each fold of the item, with the paths it wrote and the paths it
deleted, each commit, and where the work is now. It is where you start, and the files decide: read each path before
you change it.

## Where you work

Your worktree is `{worktree}`. Nothing you do is committed by you: the engine folds your changes into the main tree
when you report. Taking an item back is a change like any other: built, tested, folded and reviewed.

## What to do

1. For each path the item wrote: where the item created the file, remove it; where it changed a file that was
   there, put back what the item changed and nothing else. A later change by other work in the same file stays.
2. Put back what the item deleted or replaced, where the plan as it now is still needs it.
3. Remove the tests that exist only for what you removed. Do not remove a test to make a red result go away.
4. Look for what came to depend on the item since: a caller, a registration, a document, a configuration entry.
   Each is either yours to put right inside your paths, or something to report, with the path.
5. Build and run the tests, where the repository's tools let you: the rest has to stand without the item.
6. Where the repository keeps a registry, a row the item filed is settled by the registry's own rule, not deleted.

Where something of the item cannot be taken back without breaking work that was built on it, do not force it:
leave it, and say exactly what is left and why.

## What to return

What you removed and what you put back, by file; what you left and why; what you ran and what it showed; the
existing code you had to touch that the item did not name; and the id of every registry row you filed. What you left
because taking it back would break other work is said in your summary. What you could not do at all - a path you may
not write, a decision nobody made - is listed as what you cannot do, with what stands in the way and what would let
it be done: a person is asked about it before your work is tested or folded.

When you report, the engine builds your worktree and runs the tests itself. A red result comes back to you with the
output, so report what you saw.
