The engine built your work and ran its tests, and the result is red. Repair it.

## The red result

{source.red}

The whole logs are under `{source.runDirectory}`. This is visit {visit} of this repair.

Each entry names a leg, the phase that did not pass - the build, or the tests - and the last lines that phase printed.

## What to do

1. Read the output and find the first thing that went wrong.
2. Find the cause in your change. Put the cause right; do not make the test pass by weakening it.
3. Where a leg says it found no test to run, the filter your task gave names tests that are not there: where your
   work was to write them, write them.
4. Build and run the tests again, where the repository's tools let you.

Keep to the paths your task allows. Where the red result is not caused by your change - a test that was red before, a
tool that is missing - change nothing for it and say so, with what shows it.

## What to return

The same report as before, brought up to date: what you changed and why, what you ran and what it showed, and again
the id of every registry row filed in this work.

When you report, the engine runs the tests again.
