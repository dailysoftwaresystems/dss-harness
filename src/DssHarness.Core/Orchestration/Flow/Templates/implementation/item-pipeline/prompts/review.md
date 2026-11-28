Review this change, for your aspect.

## The class-closure record

Read this first, whatever your aspect. The classes of the decisions this item closes, each with its invariant, its one
home and its sites:

{input.item.classes}

The record of each class so far, as the last report gave it:

{implement.latest.closure}

A finding of yours that is another instance of a class the item closes - the same data decided again somewhere the
change or its class's sites reach - is not a new defect: class it `reopened`, and name the class id in `reopens`,
exactly as the classes or the record above name it. Every other finding is `introduced`, a defect the diff made. Where
the item declares no class, every finding of yours is `introduced`, and `reopens` is empty. A `reopens` that names no
class of the item's, or a severity off the schema's scale, is sent back to you to answer again.

## The item

{input.item.title}

What its builder was asked to do:

{input.item.instructions}

- The paths the item owns: {input.item.ownedPaths}
- The paths it had to leave alone: {input.item.forbiddenPaths}
- What must hold when the item is finished, its definition of done: {input.item.done}
- The existing code it was to reuse or extend: {input.item.reuses}
- What it was to create from nothing, and why nothing that exists served: {input.item.creates}
- What it was to replace because the request asked for it to be rebuilt, and what was to become of the old one:
  {input.item.replaces}

## The tests it runs

The engine tests the item on these units alone - the plan's, then those its builder added:

{input.item.tests}

{implement.latest.tests}

Where both lists are empty, this item runs no tests: {input.item.noTests}

That reason is for the `tests` aspect to judge, where it is given and both lists are empty: where the change reaches
code a test reads, or adds a behaviour no test pins, the reason does not hold, and that is a `tests` finding naming
the tests the item should run. Other aspects leave it.

## The change

It is what this run changed under the item's paths since commit `{input.orient.startCommit}`, as it now stands in the main
tree, folded and committed: `git diff {input.orient.startCommit} HEAD -- <paths>` shows it, with the item's paths after
`--`. In a later round or a repair it can also hold earlier work in the same paths: report on that only where this
item's instructions or definition of done concern it. Read the code around it in the main tree too; a change is right
or wrong in its surroundings.

This is round {iteration} of the review of this item. You are told nothing of earlier rounds but what its builder
declined, below: review what is there now.

## What its builder declined

{implement.latest.declined}

Each entry is a finding an earlier round reported that the builder did not act on, because it holds the finding
wrong, with its reason. In the first round the list is empty. For each entry of your aspect, check the reason in the
code. Where it holds, the decline stands: do not report that finding again. Where it does not hold, report the finding
again, and say in its text why the reason does not hold. Entries of other aspects are not yours.

## What to return

Whether the change has anything of your aspect, and each finding: your aspect, the file and line, what goes wrong and
in which case, what would put it right, how much it matters, whether it is in production code, and its class -
introduced, a defect the diff made, or reopened, another instance of a class the item closes, with the class id in
`reopens`. A statement of the
item's definition of done that the change does not hold is a `correctness` finding; other aspects leave it.

Report what you confirmed in the code. An empty list is the answer for a change with nothing wrong in your aspect.

## Issues that were there before

{text.issuesBefore}
