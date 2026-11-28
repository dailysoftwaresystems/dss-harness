An issue you meet that was there before this run - a bug, a broken or missing test, a stale document, outside what
this run changed - you report in `preExisting` and never fix yourself, even in a path you may write, unless your task
is to fix that issue, which you then do and do not report: its title, what is wrong, where, what you read or ran that
shows it (quoted), and whether it stops your own work. Report only what you saw yourself; where you met none, the list
is empty. A step of the run checks each one on the run's base commit and gives it its course: `fix` - fixed now, at its
root, by the run's recovery; `join` - fixed with an issue taken before it whose root cause it shares; `next` - sent to
the run's next round, a work item of which settles it; `list` - listed for the person; `duplicate` - an issue the run
has taken already; or `unproven` - not reproduced there, and not listed.
