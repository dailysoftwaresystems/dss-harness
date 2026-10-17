Your work was folded and reviewed, and the review found something. Act on it.

## The findings

Round {iteration} of the review. Each finding names the aspect that reported it, the place, what is wrong, how much it
matters and whether it is in production code:

{review.findings}

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
