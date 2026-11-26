The machine's plan check refused the work items you set out to put right the failure of `{source.step}`. Nothing of
them was built. Its problems:

{remedy-check.problems}

Where the failure is an issue that was there before the run, the issues joined to it share its root cause, and the
items settle them too - each with its id, title, description, where and proof; empty where none is: {source.joined}

Settle each of them, and say again what puts the failure right. The items you give now are checked once more and then
built as they stand, so give items the check passes: each named in lowercase letters, digits and single hyphens, with
the paths it owns - no file under two items - and every path it reuses there in the tree; each that changes a decision
gives its class - its invariant, its one home, its sites, its searches, its universe, which answers every axis with its
values or why it does not apply - and one that decides none says why in noClass; each names the tests its change
affects, or says in noTests why it affects none.

- **fix**: the work items, at most four, each sized to one builder's cycle, with the tests that show the failure is gone
  and its definition of done, of which the first statement is that `{source.step}` would now pass.
- **retry**, where the problems show that nothing needs changing after all, and the step will pass as it is.
- **person**, where what the check asks for is something only a person can give. Nobody is asked now: the step runs
  again, and you are asked again if it fails. Say exactly what is needed, and the choices.

Never weaken what found the failure to make it pass.

## What to return

The course; a summary that says what the check refused and how the items settle it; the cause; the evidence; the work
items where the course is fix, empty otherwise; the choices where the course is person, empty otherwise.
