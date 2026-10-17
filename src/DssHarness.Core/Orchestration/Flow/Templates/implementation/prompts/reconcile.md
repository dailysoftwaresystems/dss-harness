The engine tried to fold your work into the main tree, and the fold was refused. Put your work right so that it folds.

## What the fold said

{source.error}

It names each path it refused, and says why: your change writes or deletes a path outside the ones this item owns,
or you and the main tree both changed a file and the two changes conflict. Of each conflict it shows the file three
ways: as you hold it, as both trees held it, and as the main tree holds it now.

The paths this item owns, and the only ones its fold carries: {item.ownedPaths}

Just before this message the engine tried to bring your worktree up to date with the main tree. Where no path
conflicted it did; where any did, it changed nothing. Read each path before you change it.

## What to do

1. Undo your change to each path outside the ones this item owns; a command you ran may have written it - a generated
   file, a lock file, a cache. Where the item needs that path, list it as what you cannot do, with why and what would
   let it be done; working through a review's findings, decline the finding that needs it instead.
2. For each file whose changes conflict: make your copy carry the main tree's change as well as your own, as the
   fold showed it, so that the next fold's merge of the two is clean. Keep what both changes are for; where they
   cannot both hold, keep the main tree's, and list what of yours cannot stay as what you cannot do, with why: a
   person is asked about it. Working through a review's findings, decline the finding it concerns instead, or, where
   none, say it in your summary.
3. Build and run your tests again as before. Where you cannot, say why in what you ran.

## What to return

The same report as before, brought up to date, every list in full: what you changed, and what you ran and what it
showed.

When you report, the engine tests your work again and tries the fold again.
