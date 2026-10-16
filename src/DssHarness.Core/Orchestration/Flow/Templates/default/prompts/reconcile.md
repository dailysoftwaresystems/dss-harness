The engine tried to fold your work into the main tree, and the fold was refused. Put your work right so that it folds.

## What the fold said

{source.error}

It names each path it refused, and says why: your change writes or deletes a path outside the ones this item owns,
or you and the main tree both changed a file and the two changes conflict. Of each conflict it shows both sides: the
file as you hold it, as both trees held it, and as the main tree holds it now.

The paths this item owns, and the only ones its fold carries: {item.ownedPaths}

Your worktree was brought up to date with the main tree just before this message, so that it holds what other work
folded meanwhile, wherever that could be done: a path both you and the main tree changed that merged cleanly is in
your worktree as your change; one whose changes conflict was left as you had it.

## What to do

1. For each path outside the ones this item owns: undo your change to it. A command you ran may have written it: a
   generated file, a lock file, a cache. If the item cannot be done without that path, undo the change all the same,
   and list it as what you cannot do: the path, why the item needs it, and what would let it be done. The path
   belongs to other work, and a person is asked about it.
2. For each file whose changes conflict: make your copy carry the main tree's change as well as your own, as the
   fold showed it, so that the next fold's merge of the two is clean. Keep what both changes are for; where they
   cannot both hold, keep the main tree's, and list what of yours cannot stay as what you cannot do, with why: a
   person is asked about it.
3. Build and run your tests again, where the repository's tools let you.

## What to return

The same report as before, brought up to date: what you changed, what you ran and what it showed, and again the id of
every registry row filed in this work.

When you report, the engine tests your work again and tries the fold again.
