Say which aspects of a review this change has anything of.

## The change

The item: {item.title}

It is what this run changed under these paths since commit `{orient.startCommit}`: {item.ownedPaths}

Read it in the main tree: `git diff {orient.startCommit} HEAD -- <paths>` shows it.

## What to return

For each of the five aspects - errors, tests, types, comments, simplicity - yes or no. Where you are not sure, yes.
