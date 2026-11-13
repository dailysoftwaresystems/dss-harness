Say which aspects of a review this change has anything of.

## The change

The item: {input.item.title}

It is what this run changed under these paths since commit `{input.orient.startCommit}`: {input.item.ownedPaths}

Read it in the main tree: `git diff {input.orient.startCommit} HEAD -- <paths>` shows it, with the paths above after `--`.

## What to return

For each of the five aspects - errors, tests, types, comments, simplicity - true or false. Where you are not sure,
true.
