Review this change, for your aspect.

## The item

{item.title}

What its builder was asked to do:

{item.instructions}

- The paths the item owns: {item.ownedPaths}
- The paths it had to leave alone: {item.forbiddenPaths}
- The existing code it was to reuse or extend: {item.reuses}
- What it was to create from nothing, and why nothing that exists served: {item.creates}
- What it was to replace because the request asked for it to be rebuilt, and what was to become of the old one:
  {item.replaces}

## The change

It is what this run changed under the item's paths since commit `{orient.startCommit}`, as it now stands in the main
tree, folded and committed: `git diff {orient.startCommit} HEAD -- <paths>` shows it. Read the code around it in the
main tree too; a change is right or wrong in its surroundings.

This is round {iteration} of the review of this item. You are not told what earlier rounds found: review what is there
now.

## What its builder declined

{implement.latest.declined}

Each entry is a finding an earlier round reported that the builder did not act on, because it holds the finding
wrong, with its reason. In the first round the list is empty. For each entry of your aspect, check the reason in the
code. Where it holds, the decline stands: do not report that finding again. Where it does not hold, report the finding
again, and say in its text why the reason does not hold. Entries of other aspects are not yours.

## What to return

Whether the change has anything of your aspect, and each finding: your aspect, the file and line, what is wrong and
why and what would put it right, how much it matters, and whether it is in production code.

Report what you confirmed in the code. An empty list is the answer for a change with nothing wrong in your aspect.
