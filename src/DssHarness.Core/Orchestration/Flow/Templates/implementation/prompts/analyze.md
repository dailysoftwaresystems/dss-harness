Analyse this run's report and bring its pain points.

## The report

This is the engine's own report of the run, computed from its records alone. Every number in it is right: do not
correct it, and add no number it does not hold.

```json
{analysis.report}
```

## What to do

1. Read the report whole. Start from its `signals` - the slowest steps, the most expensive, the retries, the
   fallbacks, the waits for room and for a person, the cache shares, what could not be counted - and weigh them
   against the run's `totals` and its times: which of them cost the run enough that a change would have paid.
2. Where a cause is open, read the run's records, which you may read under `{analysis.records}` and nowhere else: a
   step's `step.json` and its stored stream say what it did and what it met; `questions.json` says what was asked and
   answered. Confirm what you can; what you cannot, put under `noted` with why.
3. Say where the run's time and money went, in three sentences at most, as the summary.
4. Name each pain point, ordered by what it cost. Cite every number it rests on from the report: the section
   (`theRun`, `totals`, `steps`, `criticalPath`, `errors`, `people`, `convergence`, `signals`), the row (a step's
   instance, a signal's kind, a question's id - empty where the member sits at the section's top), the member, and
   the value exactly as the report holds it, copied as the JSON writes it. A citation the report does not hold is
   refused and you are asked again.
5. Recommend the change, exactly: a configuration key as `config.json` spells it, a workflow key as the yml spells
   it, a prompt's line by its file, a repository fix by what it changes. Where the cause is not the repository's but
   the request's or the person's waiting, say that instead.

## What to return

The analysis as the schema asks: a summary of three sentences at most, the pain points ordered by what they cost,
and what you noted but could not confirm. No number the report does not hold, and no step named that its rows do not
name.
