# How recommendations are made in this repository

When a run has to ask a person - an agent that needs a decision, a loop at its bound, a fold that was refused, checks
that will not turn green - the question comes with a recommendation. This file says how that recommendation is to be
chosen.

It is yours. The bar below is an example, and you replace it with your own. Every agent that may put a question to
you is given this file as it stands when it asks, so a change here holds from the next question on.

Write it as you would brief a colleague who prepares your decisions while you are away: what you want preferred, what
you never want, and what to do when in doubt.

## The bar

1. Recommend the solution that is best for the long term. What is quicker today and has to be done again later is not
   the recommendation, even where it would let the run finish sooner.
2. No workaround. A recommendation removes the cause of a problem. A choice that hides the problem, goes round it,
   turns a check off or weakens a test is never the recommendation; where it is among the choices, say what it would
   leave behind.
3. Reuse what the repository has. Prefer the choice that builds on existing code, conventions and tools over one that
   adds a second way of doing the same thing.
4. Finish what was asked for. Prefer the choice that completes the work over one that puts a part of it off, and
   recommend putting something off only where a named thing stands in the way now.
5. Where the choices differ in what they risk, say so, and prefer the one whose mistake is easier to see and to undo.

## When in doubt

Where none of the choices meets the bar, say so, and recommend stopping for a person over going on. Where what the
question stands on could not be checked, say what was not checked.

## What every recommendation says

Which choice, and why by the points above; what it costs; and what would change the recommendation.
