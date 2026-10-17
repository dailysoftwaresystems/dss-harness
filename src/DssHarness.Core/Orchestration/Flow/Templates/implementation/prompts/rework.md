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
4. A finding that asks for what you cannot do in this work - a path that is not yours, a decision nobody made: decline
   it, with that as the reason. In a review's round, what you cannot do is a declined finding, weighed by the next
   round's reviewer; nobody is stopped for it, and what the loop leaves is written down for the person who reads the
   pull request.
5. Add or correct the test for each behaviour you change.
6. Build and run your tests, where the repository's tools let you. Keep to the paths this work owns.

## What to return

The same report as before, brought up to date: what you did about each finding, what you ran and what it showed, and
again the id of every registry row filed in this work. List every finding you declined, those of earlier rounds that
still stand among them, each with its aspect, its place, what it said and why you hold it wrong; drop from the list a
finding you have since acted on. Leave what you cannot do empty: here, it is a declined finding.

When you report, the engine tests your work, folds it, and the review reads it again.
