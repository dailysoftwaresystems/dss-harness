Your work was folded and reviewed, and the review found something. Act on it.

## The findings

Round {iteration} of the review. Each finding the review classed introduced - a defect your change made - names the
aspect that reported it, the place, what is wrong, how much it matters and whether it is in production code:

{route.rework}

Each entry of the list above that names a `reopenedClass` is not one finding but a class reopened for the first time:
the class, how many rounds have reopened it (`rounds`), how much its worst finding matters, and all its findings.

## The classes reopened

{route.homeAudit}

Each class this list holds was reopened twice or more - its count is in `rounds`: the fix an earlier round made was
wrong at its root. Do not edit the code for it first - make the home audit, reading the whole home and every consumer
the graph gives you, and deliver what makes its invariants structural, one decision, one place; record it in
`homeAudit` of the record. The check refuses the record of a class reopened twice or more that carries none.

## The class you are closing

The classes of the decisions this item changes, each with its invariant, its one home and its sites:

{input.item.classes}

The record of each class so far, as the last report gave it:

{implement.latest.closure}

Act by them. For a class reopened once, never fix the one finding alone: sweep its neighbours whole, extend the site
list and the record, and close the class - a site the list missed is yours to add and to close with the rest, and
nothing of the class is done while one of its sites is left.

## What to do

1. Act on every finding, the small ones too: what is not dealt with now comes back in the next round or is left in the
   backlog.
2. Where you hold a finding wrong, do not change the code for it: decline it, with a reason a reviewer can check in
   the code. The next round's reviewer of that aspect is given your reason. Where it holds, the finding is dropped;
   where it does not, the finding comes back, saying why.
3. A finding you declined before that has come back with the reviewer's reason: act on it, or decline it again only
   with a reason that answers the reviewer's.
4. Where two findings ask for opposite changes, act on the higher-graded one and decline the other, naming it as the
   reason.
5. A finding that needs a path that is not yours, or a decision nobody made: decline it, with that as the reason.
   Nobody is stopped for it; the next round's reviewer weighs it.
6. Add or correct the test for each behaviour you change.
7. Build and run your tests as before. Where you cannot, say why in what you ran. Keep to the paths this work owns.

## What to return

The same report as before, brought up to date, every list in full: what you did about each finding, and what you ran
and what it showed. List every finding you declined, those of earlier rounds that
still stand among them, each with its aspect, its place, what it said and why you hold it wrong; drop from the list a
finding you have since acted on. Leave what you cannot do empty: here, it is a declined finding.

When you report, the engine tests your work, folds it, and the review reads it again.
