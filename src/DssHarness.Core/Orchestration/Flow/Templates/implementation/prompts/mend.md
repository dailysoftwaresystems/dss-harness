The engine built your work and ran its tests, and the result is red. Repair it.

## The red result

{source.red}

The whole logs are under `{source.runDirectory}`. This is visit {visit} of this repair.

Each entry names a leg, the phase that did not pass - the build, or the tests - and the last lines that phase printed.

How the engine runs the tests, leg by leg: {orient.tests}

Every red result the run knows of, those from before its changes among them:

{run.red}

## What to do

1. Read the output and find the first thing that went wrong.
2. Find the cause in your change. Put the cause right; do not make the test pass by weakening it.
3. Where a leg says it found no test to run, the filter your task gave names tests that are not there: where your
   work was to write them, write them.
4. Build and run the tests again as the engine runs them. Where you cannot, say why in what you ran.

Keep to the paths your task allows. A red result that was there before this run - a test red before its changes, a
missing tool - is not yours to fix: report it in `preExisting`, quoting the line above or of the output that shows it
red before the run's changes, with `blocks` true where it stops your work. Without such a line, it is yours.

## What to return

The same report as before, brought up to date, every list in full: what you changed and why, and what you ran and what
it showed.

When you report, the engine runs the tests again.
