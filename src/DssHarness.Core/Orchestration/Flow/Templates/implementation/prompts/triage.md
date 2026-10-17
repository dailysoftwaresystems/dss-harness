Triage the request this run was started with.

## The request

{input.request}

Where nothing stands above this line, no request was typed, and the run's prioritiser chose the work. What it chose:

- Title: {priority.title}
- The work: {priority.request}
- Why: {priority.rationale}
- Blockers it named: {priority.blockers}
- Stops it named: {priority.stops}

A plan given to the run as a file: `{input.plan}`. An empty name means none was given.

## What this run already knows

- CI. Passed: {ci.passed}. Red legs: {ci.red}
- The baseline, the repository's tests on the tree as the run found it. Passed: {baseline.passed}. Red legs:
  {baseline.red}

Where a line says that a step left no output, that thing was not read or did not run in this run: it is unknown, and never a pass.

## What to do

1. Say what kind of work this is, and name it in one line: that line becomes the title of the pull request.
2. Say what the work will touch, from reading the code.
3. Say whether the request brings an implementation plan, and where it is: in the request's own text, in a file the
   request names, or in the plan file given to the run. Copy a plan that is in the request's text word for word.
4. Say whether the request itself asks for something that exists to be refactored, rewritten or created again, and
   list each such thing with where it lives. List only what the request asks for; where it asks for none, the list is
   empty, and the run then builds on what the repository has.
5. Say whether the request, in the text under "The request" above, approves its plan beforehand, and copy the
   sentence that says so word for word. Only an unmistakable statement counts. Where no request was typed, none was
   given.
6. Find what is ambiguous. Your run context says whether you may ask the person. Where you may, ask now, everything at
   once, each question with its choices and the one you recommend; where you may not, decide and record each
   assumption.
7. Name the blockers and the stops you find. A red leg or a red test in the area the request touches is a blocker;
   one elsewhere belongs in your summary. What the prioritiser named reaches the planner beside yours, so do not
   repeat it. Write each stop as a question to the person who has to lift it, with its choices and the one you
   recommend.

## How recommendations are made in this repository

{recommendations}

## What to return

The title, the kind, your summary of the request, what it touches, the plan it brings, what it asks to have rebuilt,
whether it approves its plan beforehand and in which words, what is still ambiguous after your questions and
assumptions, the blockers and the stops.

Done is when a planner could begin from your answer and the request without asking what was meant.
