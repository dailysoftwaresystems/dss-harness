The plan of this run has to be updated: a person changed something while the run was under way. Work out what the
change means, and return the whole plan again as it now has to be, in the same form, with what the change does to
the scope.

## What changed

{update.cause}

Where the lines above name a plan check and no change a person made, nobody changed the request: the items of a
round - its own, which an audit or the plan gave it, and any an earlier change placed in it - do not pass the check
that opens it, and they are in the list of work below. Settle exactly what the check found and nothing else: give the
items paths no two of them share, make each one whole as the check asks, make one item of two, or withdraw what
another already covers, each as a piece. An item of the round that you change becomes an item of the plan, under the
name it has. Leave the rest of the plan word for word. This is asked once for a round: where the items still do not
pass, a person is asked whether they are built as they are.

## The request as it now reads

{input.request}

## What has run since

{update.since}

## The work of this run, and what to call each piece by

{update.work}

## What to do

1. Before anything else, say what is different. Set what was there before beside what is there now, and name what
   the change adds, what it takes away and what it leaves as it was. Where an answer was changed, the first answer
   was acted on: read above what it set going, and count that as work that ran. A change of wording that changes
   nothing to build is a finding too, and then the plan stays as it is and `scope` is empty.
2. Read the code and the plan again where the change touches them. Whatever the change adds, search the repository
   for first, as for any plan, and keep each item's lists of what it reuses, creates and replaces true. Write each
   test filter as for any plan, in what each leg's test runner takes after its filter option; the legs, their
   runners and that option: {orient.tests}
3. Change the plan where the change requires it, and nowhere else. What was approved and is not touched stays word
   for word, so that what passed still passes and whoever holds the old plan finds the new one familiar.
4. Say what the change does to the scope, under `scope`, one piece for each work item it touches. Nothing you write
   there is carried out by telling somebody: each piece becomes work of the run, done by a step of its own, and
   built, tested, folded and reviewed as any item is.
   - `added`: an item the plan did not have. `item` is the item, exactly as it stands in `items`: sized to one
     builder's cycle, never more and never a sliver of one, with its own definition of done.
   - `changed`: an item the plan had, that is now to be done differently, whether it has not started, is being
     built, or is built and committed. `item` is the item as it now stands in `items`, under the name it had.
     Where it was built, a step does it again from what is there: say under `keep` what of the work stays, under
     `undo` what is taken back, and under `instead` what is done in its place. Its definition of done says what holds
     once it is done again.
   - `removed`: an item the plan had, that is withdrawn. It is no longer in `items`. `item` is the item under the
     name it had, written for the step that takes it back: its instructions say what to take back; its definition of
     done says what must hold afterwards; its owned paths are the paths that step may write; its test filter selects
     the tests that show the rest still stands, or is empty, which runs every test.

   Work that is done and that the change does not reach gets no piece. `changed` is never empty: it says what the
   change means for this piece, for whoever carries it out, who has none of this conversation. `for` is empty in
   these three.
5. Say who has to know without any scope of their own changing, as pieces of the kind `told`: `for` is the id of
   work under way, exactly as the list above gives it, `item` is null, and `changed` says what they are to know. A
   work item that has a piece of another kind needs none: whoever is working on it is told that piece. Where nobody
   has to know, write none.
6. Where the change leaves something open that changes the plan, your run context says whether you may ask the
   person. Ask everything at once, each question with its choices and the one you recommend by the bar below; or
   decide, and record what you assumed.

## How recommendations are made in this repository

{recommendations}

## What to return

The whole plan, in the form of every plan of this run: its summary, which now also says in a sentence what the change
was and what it did to the plan; its definition of done, changed where the change reaches it; its source and its
departures as before; the items as they now are, each with its definition of done; and `scope`.

The plan is checked, audited and put to approval again before anything of it is carried out or anyone is told.
Until then the work under way goes on with the plan as it was.
