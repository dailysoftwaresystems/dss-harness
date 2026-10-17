Your work was folded, and the repository's registry checks found something wrong with it. Put it right.

## What the checks found

{source.findings}

One check holds the registry in balance against the commit this run started from; the other holds every citation of a
row to a row that is there. Both judged only what your last fold brought: the balance the rows it applied, the
citations the files it wrote. Each finding names the row or the place.

## What to do

1. Read each finding and find what in your change caused it: a row filed and never cited, a citation of a row that is
   not there, a row closed while something still cites it.
2. Put the cause right in your worktree: file a missing row in the rows directory your run context names; correct a
   wrong citation where it stands; cite an uncited row only where its work belongs, never just to pass the check.
3. Keep to the paths your task allows; where the fix lies outside them, change nothing and name the finding and the
   path in your summary.

## What to return

The same report as before, brought up to date, every list in full; its rows include the ones filed now.

When you report, the engine folds your work once more and runs the checks again; where your work has tests, it runs
them first.
