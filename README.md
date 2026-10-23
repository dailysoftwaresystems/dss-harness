# DssHarness

One cross-platform CLI for a repository's worktrees, builds, tests and cross-host
work — configured entirely from a file in the repository, not compiled into the tool.

```bash
dotnet tool install --global dssharness
dssharness --help
```

To update it later, on a machine anything else may be running it on:

```bash
dotnet tool exec DssHarness --yes --source https://api.nuget.org/v3/index.json -- update-tool --wait
```

That runs the newest release beside the installed one and updates it only once no `dssharness`
process is running on the machine, naming each it waits for. A bare `dotnet tool update` typed
beside a running command takes the tool away from every command typed meanwhile, and on Windows
fails. `dssharness update-tool` is the same door typed short, where the system lets a running
program be replaced; on Windows it changes nothing and says the line above, with the options it
was typed with. Once the update has begun the command does not stop it, and says it done only once
the tool is listed as the new release.

## Why

A repository's build and test work usually accumulates as a pile of paired
`.sh`/`.ps1` scripts. They drift apart, they encode one repository's facts, and they
fail differently on every platform — a Windows path limit here, a frozen macOS bash
there, a missing `rsync` somewhere else.

`DssHarness` replaces that with a single tool whose behaviour is declared in
`.harness-config/config.json`. **Nothing about a specific repository, language or
toolchain is built into the tool.** If a behaviour cannot be expressed in
configuration, that is a defect.

## Getting started

```bash
dssharness verify-git     # is git installed, and is this a repository?
dssharness init           # create .harness-config in this tree and seed config.json
dssharness legs           # where each leg can run, or why it cannot
dssharness help           # reference material: exit codes, config, legs, layout
```

Every CMake configure is asked which compilers it resolved, and each leg's line names them —
`compiler: MSVC 19.51.36231 (C, CXX)` — so every verdict says which compiler produced it; a
toolchain's `compilerId` fails a leg CMake configured with another. A language only a dependency's
`project()` enables — C, where a C++ project fetches googletest — is identified from CMake's own
record of it, because CMake's answer gives it no id.

`init`'s `msvc` toolchain names the `visualStudio` developer environment, so an MSVC leg builds from
a plain shell: the host that runs it runs Visual Studio's own `vcvarsall.bat` for the leg's processor
and starts every process of the leg in what it set, and the leg's line names the instance and tools
it used. A host without Visual Studio's C++ build tools turns the leg away as a tool missing.

`init` inspects the repository and seeds a configuration that already matches it —
a CMake project gets toolchains and `ctest`, a .NET solution gets `dotnet test` — with
legs for the operating system and processor of the machine it ran on. With no project
detected it seeds no legs, and `legs` fails until some are declared.

## Commands

| Command | Does |
|---|---|
| `init [--install-tools]` | Create `.harness-config` in the tree it runs in, a worktree's included, seed `config.json`, add ignore rules; installs tools only when asked |
| `verify-git` | Check git is installed and this is a repository |
| `create-worktree <name>` | Create a worktree (`--random` generates the name) |
| `delete-worktree <name> [--force]` | Remove a worktree and everything under it, its copies on hosts, and the copies kept in each - the mutation workers in `.mutations`, and on this machine the leg copies in `.legs`; refuses one holding work that would be lost, a locked one, one whose evidence directories hold measurements, or one with a copy in it that a sweep or a run still holds or that cannot be removed, without `--force` |
| `list-worktree [--hosts] [--json]` | List existing worktrees with the commit each was made from, the copies hosts keep of them, and the copies left by worktrees that are gone; `--hosts` also asks each host what it keeps, and how large each copy is |
| `create-orchestrator <o> --model <id> [--parallel N]` | Create an orchestrator under `.orchestrators`; `--parallel` (4 unless given) is the most agents with a worktree at once |
| `create-agent <o> <a> --model <id> [--empty \| --no-worktree]` | Create an agent: its record, its worktree at `<worktrees.root>/<o>/<a>`, and its seed, the main tree's uncommitted state handed to it; `--no-worktree` makes the engine's own kind, a record and two directories that read the main tree and record no session - a person has no use for one by hand |
| `seed-agent <o> <a> [--empty] [--force]` | Seed a live agent again, with every path the main tree holds otherwise than the agent shares it, committed or not; refused over changes of its own without `--force`; a file written after it was weighed stops it, exit 21, never written over |
| `refresh-agent <o> <a> [<path>... \| --all] [--except <path>]... [--stale <path>]... [--merge <path>]... [--apply] [--json]` | Copy into a live agent every path under the paths - the anchor registries' directory by default, anywhere in the tree with `--all` - that the main tree holds otherwise than the agent shares it, committed or not, recorded as handed to it; a path the agent already holds as the main tree does is recorded as shared; refused over a path the agent changed, naming each, unless `--except` leaves it as the agent changed it, `--stale` hands it the main tree's copy where the main tree's history holds the agent's very bytes at that path, or `--merge` writes a clean three-way merge of the two into its worktree as its change, the path recorded as handed the main tree's copy; the arm registry both changed is merged by arm, unasked, and taken where the result reads as a registry; a file the agent's session wrote after it was weighed stops it, exit 21, never written over |
| `rebase-agent <o> <a> [--apply] [--json] [--merge <path>] [--settled <path>]` | Move a live agent's base to the main tree's HEAD: what the main tree committed since comes in as git holds it, its own changes and what it was handed stay; a path both changed is refused with what a three-way merge of it gives, unless `--merge` names it and its merge is clean - written into the worktree as the agent's own change on the new base - or `--settled` keeps the copy reconciled by hand; the arm registry is merged by arm, unasked, and taken where the result reads as a registry; refused over anything of its own what comes in would go over, a file renamed in case alone over the agent's copy at its old name, or its deletion of it, among it; a file the agent's session wrote after it was weighed stops it, exit 21, never written over; run again after one that stopped part way - the command its last words name - it finishes it |
| `fold-agent <o> <a> [--apply] [--json] [--merge <path>] [--settled <path>] [--new <ID>] [--accept-lost <ID>:<cell>] [--accept-over <ID>:<cell>]` | Fold an agent's own work into the main tree and apply the rows it filed, every refusal named before anything is written and the rows all or nothing; a path both trees changed is refused with what a three-way merge of it gives, and a clean merge written into both trees only where `--merge` names it - the arm registry is merged by arm, unasked, and taken where the result reads as a registry; a path reconciled in the main tree by hand and named with `--settled` is handed to the agent as the main tree holds it and recorded as shared; a row is made only where `--new` names it, and a cell that does not keep its stored text written only where `--accept-lost` names it; a row an earlier fold applied is weighed cell by cell against what the registries took since - their change kept where the agent made none, and a cell both changed otherwise written only where `--accept-over` names it; a rename of case alone the agent made is refused, saying to `git mv -f` it in the main tree, and once made there is weighed against the base's file under its old name; its worktree is kept |
| `delete-agent <o> <a> [--apply] [--json] [--merge <path>] [--settled <path>] [--new <ID>] [--accept-lost <ID>:<cell>] [--accept-over <ID>:<cell>] [--discard-uncommitted]` | Fold what is left and apply its rows, keep its evidence and transcripts, then remove its worktree and its copies on hosts; an agent with no worktree is marked deleted in one write, nothing folded and no transcript looked for |
| `list-orchestrator [<o>] [--steps] [--questions] [--json]` | List orchestrators, their agents and where each stands; for a run, its own lines from `flow.json` and what its engine is doing - none, running, pausing, recording what a complete pause left running, or gone - each step instance with its state, visit, attempt, agent and directory with `--steps`, and each question and what became of it with `--questions` |
| `run-orchestrator <workflow> [--run <name>] [--input k=v]... [--json]` | Start a run of a declared workflow: validate it, make the run - `r<N>`, one more than the highest there, or `--run`'s name, refused where it is taken - claim it for this process, and drive it until nothing more can run: done `0`, failed `20` (`21` where every failure that ended it was incomplete), paused on a question `1`, paused or stopped on request `130`; `k=@<file>` gives a text input a file's text, relative to `--directory`. Refused (13) while another run of the repository is unfinished with work in the tree |
| `resume-orchestrator <run> [--json]` | Go on with a run from its records alone, where it stood - what a stop, a crash or an answer left - under the definition it began with: what was cut off, and a failure that ended the run, run again (a failure answered `fail` stands), what the run owed is made first, and every question and answer is kept; it goes on beside an open question, and is refused (13), having written nothing, only where nothing can run until one is answered (a budget question open pauses it instead); refused (13) too where the repository's entry for it changed since, or where a record the run is made with is missing, named |
| `answer-orchestrator <run> <answer> [--question <id>] [--text <text> \| --text-file <file>] [--again] [--json]` | Answer a question of a run: handed to the engine running it through `inbox.json`, or else a session of its own; `--again`, which changes an answer, is refused in this build |
| `pause-orchestrator <run> [--all] [--json]` | Ask the engine running a run for the safe pause through `stop.json` - its agents hand off, the machine steps finish unless `--all`, then the run is paused - and wait until it is complete |
| `stop-orchestrator <run> [--now] [--json]` | Ask the engine running a run to stop where it stands, and wait 30 s; `--now` ends the engine's process - only on this machine, and only the process its owner names by its start |
| `validate-workflow [<workflow>] [--json]` | Check the workflows `workflows` in config.json declares: that each yml definition reads, holds and names files that are there (`help workflows`). Nothing is run |
| `export-workflow <workflow> --to <dir> [--shipped] [--json]` | Copy a workflow - its yml and every file it names - into `<dir>/<workflow>/`: a declared one once it holds (12 where it does not), byte for byte, or with `--shipped` the one this tool ships, to make a version of your own. Refused (13) where that folder is there; a file that cannot be written takes back what was written |
| `list-agent-providers [--template] [--json]` | List the agent sections config.json declares: each provider, its keys' names in the order they are tried with whether the slot holds each - `present` or `missing`, never a value - its models and whether each is priced, the tiers and the clients; exit 0 whatever is missing; `--template` prints the sections as `init` writes them into a new config, to paste into an existing one (`help agent-clients`) |
| `delete-orchestrator <o> [--delete-evidence]` | Delete an orchestrator once no agent of it stands in the way - a live one with no worktree goes with it - and nothing is left below its directory under the worktrees root - no worktree, and no directory with a `.git` of its own, each named and refused (13) until then; its agents' evidence, transcripts, steps and sessions go only with `--delete-evidence` |
| `check-root-litter` | Report files left loose at the root of the checkout, ignored ones included |
| `write-anchor <id> --priority P --trigger TEXT` | Add an anchor: to the pending registry, or to done when closed |
| `set-anchor <id>` | Change an anchor; changing its status moves it between registries |
| `read-anchor <id>...` | Show anchors in full |
| `read-anchors` | List anchors, or check the registries with `--lint` |
| `check-anchor-balance` | Fail a change that creates more anchors than its work closes |
| `check-anchor-citations` | Check every anchor cited in the declared roots resolves to a row |
| `fix-line-endings [--all \| --changed]` | Apply the line-ending policy `.gitattributes` declares; `--check` refuses instead |
| `check-ci-legs` | Report each CI leg, separating a real failure from a budget overrun, by the job and step names `ci` declares (`help ci`) |
| `legs [--legs a,b]` | Measure the hosts and show where each leg can run, or why it cannot |
| `install-missing-tools [--legs a,b] [--dry-run]` | Install or update what each configured leg's host is missing; `--dry-run` names each command and runs none |
| `update-tool [--wait] [--tool-path <directory>]` | Update this machine's installed DssHarness to the newest release, only while no other `dssharness` process runs here: refused naming each (13), or waited for with `--wait`; refused while another update changes the same directory (13, with `--wait` or not); on Windows run it from beside the installed tool, as above (`help legs`) |
| `sync` | Put a host's copy of this tree in step with it, deletions included: each worktree has a copy of its own; `--dry-run` lists what it would write and delete and exits as the sync would; `--pull <path>` instead brings a file back from it, or every file below a directory named with a `/` at its end |
| `build [--legs a,b] [--time]` | Build every selected leg, in its own variant-keyed build directory |
| `test [--legs a,b] [--time] [--set-up]` | Build and test every selected leg, with a witness for each verdict; `--set-up` brings up the set-ups its test invocation names around each leg's work, on the machine that runs it, and down after (`help setups`) |
| `run <runner> [--legs a,b] [--time] [--input name=value] [--set-up]` | Run a predefined runner across the legs it declares, giving its action's inputs values for this run; `--set-up` brings up the set-ups the runner and its run checks' runners name around each leg's work, and down after (`help setups`). A run line names what only the tool knows of a leg - its build directory, the file its build makes, and the compiler its build identified, `{compiler_C}` or `{compiler_CXX}`, which is gcc on one leg and cl on another - and a step naming one of those has its leg built first (`help runners`) |
| `check-mutations [--legs a,b] [--arms a,b] [--self-test] [--list] [--pending]` | Prove each selected leg's tests can fail: in worker copies of its tree, mutate each arm the registry declares, build it, witness every object depending on the site rebuilt, run its test binary whole and judge what reddened; `--self-test` sweeps the fixture the tool carries instead, through each leg's toolchain; `--list` says, without sweeping, each arm's last verdict on each leg and whether anything it was judged on has moved - of what a test binary reads at run time, only the files `mutations.runReads` names for its runner; `--pending` sweeps only the arms with no passed that still holds, and with nothing pending passes, sweeping nothing (`help mutations`) |
| `stop-run <run> [--now] [--json]` | Stop a `build`, `test`, `run` or `check-mutations` run from another terminal, typed in the tree that ran it: asked of its dispatcher, which stops every leg where it stands as an interrupt does and writes its ledger, and waited for 60 s - said stopped only where its dispatcher took the stop; `--now` ends the dispatcher's process where it stands, with all it started - only on this machine, and only the process its owner file names by its start. On Windows a dispatcher that ends in any way, a kill among them, ends its local legs with it |
| `clean [--legs a,b] [--dry-run] [--former-workers]` | Remove each selected leg's build directory, the mutation workers its sweeps and self-tests keep in its tree, and the copies of its tree it works in (`.legs`), wherever the leg runs, so a full disk can be freed; `--dry-run` says what each holds and exits as the clean would; `--former-workers` removes only the workers an earlier version kept beside the repository's trees, here and on each host (`help space`) |
| `up <name> [--input name=value] [--json]` | Bring a set-up up on this machine: run the `up` command line `setUp` declares for it, at the root of the tree - a program and its arguments, no shell, a line per platform where they differ, its declared inputs filled in with what `--input` gives over their defaults (`help setups`) |
| `down <name> [--input name=value] [--json]` | Bring a set-up down: run its `down` command line there, with the values it was brought up with; refused (13) while a leg of a run on this machine still uses it in that tree - in any of the repository's, for one the machine shares - whose last leg brings it down |
| `host-exec --ssh <name> \| --wsl [<distro>] -- <command>` | Run a DssHarness command on an ssh host or in a WSL distribution |
| `help [topic]` | Explain exit codes, configuration, legs, disk space, heavy-leg admission, worktrees, orchestrators, workflows, the codebase graph, agent clients, anchors, layout, secrets, tools, runners, set-ups, verdicts, mutation testing and CI legs |

Every command takes `-C, --directory <dir>` and `-v, --verbose`.

Zero means every selected leg reached a verdict and none failed. A run where nothing failed but
some leg never reported, or left a set-up it brought up running, exits `21` and names those legs: a leg that did no work proves nothing
about the code, so it is never counted among the legs that passed. A command that changes an
orchestrator or an agent exits `21` too when it stops part way - an agent closed and its worktree
not yet removed, a fold or a hand-over half written, a move of an agent's base half made, an
orchestrator half deleted; run again, it finishes. Without `--apply`, `refresh-agent`,
`rebase-agent`, `fold-agent` and `delete-agent` are dry runs, and a dry run exits as its `--apply`
would: `0` only when that `--apply` would go through, `10` where its arguments are a usage error,
which that `--apply` answers before it takes anything, `13` where it would be refused - a consent
nobody gave, or a run holding what the `--apply` needs, included - and `21` where a deletion's
`--apply` would go as far as the removal and leave it unfinished, over a copy a run or a sweep
holds or copies kept in the worktree it cannot remove as they stand. A run holding a tree the
`--apply` takes, or a host's copy of the worktree, is named on a `held now:` line, and one holding
a leg copy or a mutation worker kept in the worktree in that copy's own words; `--json` answers
with one document whose `request` member says what is wanted of you - `sound` (nothing; wait
where `heldNow` names a holder), `needs-consent` or `refused` - and whose `heldNow` holds each
holder, those copies' among them. Beside them `needs` names each consent the request lacks
(`fold-new-rows`, `fold-lost-rows`, `fold-over-cells`, `fold-two-changes`), `refusals` each reason
it is refused for (`conflict` where a path both trees changed has a merge that conflicts, `other`
for anything else), `folded` what a fold's `--apply` wrote, merged, handed and applied, and `planned` what the
verb's plan names - what a rebase takes and keeps, a refresh or a fold hands, each merges, a fold writes and deletes,
the registry files its rows change and the rows it applies - and what each consent it lacks and each conflict it meets
is about. A refresh or a rebase over a path both trees changed whose merge is clean lacks `--merge` as a fold does. An
`--apply` refused at its hold answers the same document, `sound`, with the run it met in
`heldNow`. Either way a dry run still prints all it weighed.
`dssharness help orchestrators` explains seeding, rebasing, folding and deleting agents, and the five verbs of a run.

A run's session - `run-orchestrator`, `resume-orchestrator`, `answer-orchestrator` - takes its terminal's interrupts
itself: the first Ctrl+C (off Windows, SIGTERM or a hang-up as well) asks the safe pause - the agents hand off, the machine
steps finish, then the run is paused - and starts no clock; the second stops the run where it stands: a step whose runner
does not end within 90 s is recorded stopped all the same, and the command ends within 120 s whatever is left - the
engine's 90 s and 30 s to write the end - which `resume-orchestrator` goes on from. Every session writes the run's report
(`report.md`, `report.json`) and names each record it wrote, in text and in `--json`.

`--time` pulls each phase's own timing marks out of its output, using `buildTimingRegex`,
`testTimingRegex` or `runTimingRegex`, and prints them under the ledger as a `TIMINGS` block naming
the leg and phase that reported each one. `host-exec` needs no `--time` of its own: everything after
`--` is the command run on the host, so `host-exec --ssh vps -- test --legs a,b --time` asks for it
there.

## Running legs at once

Every leg-running command dispatches its legs in parallel and waits for all of them, reporting each
one live rather than buffering until the end. Output is written a whole line at a time, and every
line says which leg it came from — a streamed child line under `-v` is tagged `<leg>/<phase>:`, so
three hosts building at once stay readable.

Legs are chunked by the **physical machine** they run on. A local leg and every WSL leg are one
machine, because WSL runs on it; each ssh host is its own. `defaults.maxParallelLegs` caps how many
run at once *on any one machine*, so a busy laptop is not asked for more than it has while the
remote hosts sit idle, and `defaults.maxParallelLegsTotal` caps the whole fleet for what it shares
even when its machines do not — a license server, a network share, a sync's bandwidth.

Both are counted by one command. Where a machine declares `admission` — under `defaults`, or its own
under `hosts.local` or an ssh host — its heavy legs (a build, a test, a runner that requires the build
or says `"heavy": true`, a run of an action step or a runner's own phase that names `{product}`,
`{buildDir}` or one of the leg's compilers — which builds its leg first — or of a step that says
`heavy: true`) share it across every
command this user runs there: each waits for one of its `heavyLegs` slots, in the order they asked,
then for the memory in use to fall below `maxMemoryPercent`, then - where its build's need is known -
for room on the filesystem it fills beside what the other admitted legs claim, says who holds each slot
while it waits, and names on its line how long it waited, the memory it started at and the room it
claimed. A WSL distribution's legs take this machine's slots, and claim room on the drive where WSL
keeps its disk; an ssh host takes its own. One that waited `maxWaitMinutes` - a wait with only its own
command's legs ahead of it not counting - is `not-admitted`, exit 7, naming what held the slots and
where they are recorded, the memory it waited on, or the room and who claimed it, and nothing of it
ran. On Windows, a leg about to wait on the memory has WSL's page cache dropped first - as root, at
most once a minute - and reads the memory again once what was dropped has come back, a minute later
at the most; a WSL leg drops it as it ends. While a heavy leg builds, each filesystem it fills - for a
WSL leg, the Windows drive its disk grows on too - is held to `minFreeGiB` (2 GiB): one that falls
under it, read every 15 seconds and again as a build or a write of its fails, stops the build,
`stopped`, what it built left for `clean`; a build under it as it starts writes nothing. Run
`dssharness help admission` for the rules.

## Design

Three principles the implementation actually holds to:

**Fail loud.** Zero always means success, and "the thing you asked about failed"
never shares an exit code with "the harness could not run" — the remedies differ.
Run `dssharness help exit-codes` for the full table, which is generated from the
code rather than written by hand. A configuration file with an unknown key, a key
written twice, a list given empty where leaving it out means every one or a set the tool chooses, or a reference to
something undeclared is rejected when it is read, with every problem listed at once.

**One behaviour everywhere.** Windows, macOS and Linux run the same code path. The
operating system is learned in one platform layer, beside a few primitives that each ask
the runtime for one fact and are named in the [architecture](docs/architecture.md);
everything above that layer is platform agnostic. CI runs the whole suite on all three, on
both x86_64 and arm64.

**Witness the work, never the exit code.** A build passes only when every file the project's
`buildOutputs` names is there afterwards — a build tool exits 0 having produced nothing often
enough that the code alone is not evidence. An entry is a path, or a mapping of platform to path
where the platforms disagree about what the same target is called (`app` against `app.exe`), and
an entry naming no path for a platform some leg builds on is refused when `config.json` is read.
A test passes only where its runner printed its `successPattern`, and `countPattern` reads how
many tests ran: a leg that ran a different number than the other legs of its project and
`testSet` is marked on its own line, never as a timing and never changing its verdict. A set
that differs on purpose, such as a platform's own tests, is named with `testSet` on that
platform's test invocation.

**Refuse early, with the arithmetic.** `create-worktree` will not create a worktree
whose build paths cannot fit inside Windows' path limit, because that failure
otherwise appears as compile errors in files the worktree never touched. It says how
long a name would fit instead. The name limit and both sides of the budget are set
in `config.json`.

## Layout

```
.harness-config/config.json                  tracked; the whole contract
.harness-config/workflows/<name>/<name>.yml  tracked; one directory per workflow, its definition
.harness-config/workflows/<name>/...         tracked; every file that definition names - its agents, prompts, schemas
.harness-config/workflows/.env/              contents ignored, .gitkeep tracked; the agent clients' keys: NAME=value files, every file merged, no key read from anywhere else
.harness-config/runner/actions/<name>/<name>.yml   tracked; one directory per action
.harness-config/runner/actions/<name>/...          tracked; whatever its steps run
.harness-config/runner/.env/                 contents ignored, .gitkeep tracked
.harness-config/runner/.secrets/             contents ignored, .gitkeep tracked
.harness-config/sshItems/<name>/.env         ignored; address, user, port
.harness-config/sshItems/<name>/.key         ignored; the private key
.harness-config/wslDistros/<name>/.env       ignored; the distribution and its credential
.worktrees/                                  contents ignored, .gitkeep tracked; the worktrees
.orchestrators/                              contents ignored, .gitkeep tracked; orchestrators and their agents
.orchestrators/<o>/agent.json                ignored; the orchestrator's record
.orchestrators/<o>/logs/<name>.jsonl         ignored; a line for each run that reached it or one of its agents, refusals included; a dry run writes none
.orchestrators/<o>/plans/<name>/             ignored; its plans, and each agent's
.orchestrators/<o>/work/<agent>/             ignored; an agent's scratch and task files
.orchestrators/<o>/agents/<agent>/           ignored; an agent's record, seed, the bytes it shares, rows, the rows applied, kept evidence
.orchestrators/<o>/agents/<agent>/steps/<instance>/   ignored; one step the agent runs, or that runs in or for its worktree: everything of the step, its handoff.md where one stopped it
.orchestrators/<o>/agents/<agent>/sessions/<id>/      ignored; API clients only: one conversation with the provider, in the provider's own form
.orchestrators/<o>/steps/<instance>/         ignored; one step of a run that no agent owns, the create-agent that makes an agent included
.orchestrators/<o>/.../<instance>/children/<session>.json   ignored; in either kind of step, Linux and macOS: a session a contained child of the step leads while it runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what carries a leg to a host leads while anything is in it, ended by the next command over the orchestrator - and by stop-orchestrator --now - once the process that started it has gone
.mutations/                                  contents ignored, .gitkeep tracked; the tree's mutation workers, in every tree
.mutations/<key><mark>-<n>/                  ignored; a worker, a repository of its own, and its claim beside it
.legs/                                       contents ignored, .gitkeep tracked; the copies of the tree its legs work in, in every tree
.harness-config/runs/                        ignored; one directory of logs per run
.harness-config/lock.json                    ignored; records in-progress runs
.harness-config/mutation-record/             ignores itself; each leg's verdicts of its mutation arms, with what each was judged on
.plans/_deferred-anchor-registry.md          tracked; live anchors
.plans/_deferred-anchor-registry-done.md     tracked; closed anchors
```

The worktrees root is `worktrees.root` in `config.json`, shown here as `init` writes it. A
configuration that names none keeps worktrees under `.harness-config/worktrees`, which spends
15 more characters of the path budget before a worktree's own name.

A sweep keeps its workers in the tree it sweeps, and a leg that works in a copy keeps it in its
tree's `.legs`; either is refused (exit 13), nothing made, in a tree whose git does not ignore
that slot: run `init` once in each repository and commit what it writes - each agent's branch
needs that commit before it can sweep or have a leg work in a copy. Workers an earlier version kept
beside a tree, `<tree>.mutation-*`, on this machine and on every host, are no workers to a sweep:
`clean --former-workers --dry-run` lists them with their sizes, beside each tree of the repository
here and on each host a selected leg runs on, and beside each one deleted since, and
`clean --former-workers` removes them - one a
sweep of that version still claims is kept. Until then `list-worktree` warns of one beside a
worktree, and `delete-orchestrator` is refused (13) while one is beside an agent's worktree. Each
worker's build is made once again.

Secrets and the run lock live only in the main checkout. A worktree receives the tracked
part of `.harness-config` through git but never the ignored part, so both resolve back to
the originating checkout. What a tree makes for itself - its runs' records, its mutation
workers in `.mutations`, its legs' copies in `.legs` - is ignored too, and kept in that tree.
The run lock knows a tree on this machine by where it leads, every link followed, so
worktrees kept on another disk through a worktrees root that is a link, and copies kept
through a `.legs` or `.mutations` that is one, are held, and asked about, as one tree however
a command spells them - a build before this one's too, since the lock keeps each tree as its
run spelled it.

## Legs and hosts

A leg says what it needs, never where it runs: an operating system, a processor, and
optionally the emulator it runs through, which rules out running it natively. Hosts are
measured before anything starts: this machine first, then WSL distributions and ssh hosts
only for the legs this machine cannot take, and a WSL distribution only for a Linux leg.
Each leg runs on the first host that can take it - its operating system, its processor and
its emulator - and is turned away there when that host lacks a program its command requires.

```json
{
  "defaults": { "localCopy": true },
  "hosts": {
    "wsl": { "Ubuntu": { "repositoryPath": "~/src/app" } },
    "ssh": { "mac-mini": { "repositoryPath": "~/src/app", "env": { "CCACHE_DIR": "/Users/dev/.cache/app-ccache" } } }
  },
  "emulators": {
    "rosetta": {
      "hostOs": "macos", "hostProcessor": "arm64", "processor": "x86_64",
      "launcher": ["arch", "-x86_64"],
      "witness": { "command": ["/usr/bin/uname", "-m"], "pattern": "^x86_64$" }
    }
  },
  "legs": {
    "linux-release": { "os": "linux", "processor": "x86_64", "config": "release" },
    "mac-x64-release": { "os": "macos", "processor": "x86_64", "emulator": "rosetta", "config": "release", "localCopy": false }
  }
}
```

```bash
dssharness legs                                       # every leg: where it runs, or why it cannot
dssharness legs --legs linux-release,mac-x64-release
dssharness host-exec --ssh mac-mini -- verify-git
```

Where this machine runs a leg itself, it works in its tree or in a copy of it kept in the tree's
`.legs`: said by `--local-copy` or `--in-place` on the command for one run, then by a runner's
`writesTree`, which works in place and is refused with `--local-copy` or on a leg a host would
run, then by the leg's own `localCopy` - `false` on a leg turns a
`true` default off for it - then by `defaults.localCopy`, and by none of them, its tree. The copy
is made from the run's one reading of the tree and kept
warm between runs, so a fold, a commit or an edit while the leg runs ends nothing of the leg's;
the first build in one starts cold, files git ignores are not in it, and `clean` removes it
(`help legs`).

No leg, `host-exec` or sync a host runs for this machine outlives the command that asked for it.
Stopping that command ends its legs on every host at once; and a command killed where nothing
closes its end of the connection leaves legs that are cancelled within two and a half minutes, on
an ssh host as in a WSL distribution, because the host stops hearing the beat this machine writes
while it is there (`help legs`). Only what holds a host awake between commands
(`holdAwakeSeconds`) is started to outlast the command that asked, and ends on its own seconds.

A host's section can also give its own `buildCores` and `testCores`, and an `env` that every
process a leg starts there sees - each build phase, the test runner, each step of a runner - as
the lowest layer, beneath the variant's, the test invocation's and the runner's own. A `PATH`
set there is where that host finds those programs, so none of them is required of it before a
leg starts: each is the run's to find - except in a developer environment, whose `PATH`, built
over the host's, is looked in before the leg starts. A directory a tool's `minVersion` raised
goes ahead of it all the same (see below).

A toolchain can name a developer environment that its legs start in, declared once and set up on
the host that runs each leg, over that host's `env` and beneath everything more specific:

```json
{
  "developerEnvironments": { "visualStudio": { "kind": "visualStudio" } },
  "toolchains": {
    "msvc": { "platforms": ["windows"], "generator": "Ninja", "env": { "CC": "cl", "CXX": "cl" }, "developerEnvironment": "visualStudio" }
  }
}
```

Every host a leg might land on is asked, through Visual Studio's installer, whether it has an
instance with `requiresComponent` - the C++ build tools unless another is named - and one without
turns the leg away as a tool missing; one that could not look leaves it unavailable. On the host
that runs the leg, the instance its survey found has its `vcvarsall.bat` run once for the leg's
processor, cross-compiling where the host's differs, and what it set is what the leg's build, tests
and runner steps start with. A `vcvarsall.bat` that fails, prints an `[ERROR`, or sets up another
processor fails the leg before anything of it starts. The programs the leg starts are then looked
for on the `PATH` it set up - Visual Studio carries `cl` and `link`, and CMake and Ninja with its
CMake component - and one missing there skips the leg as a tool missing, named, before anything of
it starts. The leg's line names the environment -
`developer environment: visualStudio (Visual Studio 18.0.11205.157, MSVC 14.50.35717, amd64)` - and
`--json` carries it as `developerEnvironment`.

An ssh host's connection data lives in its own directory under `.harness-config/sshItems/`,
which git ignores: an `.env` naming the address, the user and the port, a `.key`, and a
`known_hosts`. `config.json` declares only the directory names, under `sshItems`, so the
tracked configuration never carries an address, a user, a key path or a credential. A WSL
distribution has the same shape under `.harness-config/wslDistros/`. ssh runs in batch mode,
so it never waits at a prompt, and a key other users can read is refused before anything
connects, with the command that fixes it.

Every WSL distribution and ssh host runs DssHarness itself, installed from nuget.org at
this machine's exact version: a host that is behind is updated, never downgraded. Betas are
released on GitHub rather than nuget.org, so only a stable build can bring a host to its
version. `install-missing-tools` installs the .NET SDK, and everything `tools` declares an
`install` for, on every configured leg's host; a `tools` entry with no `install` is an
allowlist entry, reported when missing and never installed. An install that needs a superuser
takes the password from that host's own `.env`, as `SUDO_PASSWORD`, and asks for it at the
terminal when none is declared — once per host, held in memory for that command alone and never
written anywhere. A run with no terminal, a run answering with `--json`, and a run given
`--no-prompt` all refuse instead, naming what would fix it; running the harness as root needs no
password at all, which is usually the answer in CI. An entry may name the platforms it
is needed on — `"platforms": ["windows"]` — and a host whose platform it does not name is never
asked about it, so a repository can declare both a Windows compiler and a POSIX one. It may
narrow that further, to `toolchains`, `legs`, `processors` and `emulators`, so `cl` scoped to
`msvc` is never reported missing on a MinGW leg of the same machine. A `minVersion`, written
`major.minor.patch` and read with the entry's `probe`, holds every copy a leg could start: the
first copy that meets it - on the `PATH`, then in the searched directories - is the one every leg
on that host starts, its directory put ahead of the `PATH` where an older copy comes first, and a
leg the tool is declared for is turned away where every copy is older, naming each and the
`install-missing-tools` command that installs one (`help tools`). Each leg is told about a tool
as it will find it, by the survey `legs` runs on its host: a leg whose toolchain names a developer
environment, on the `PATH` that environment sets up there for its processor, so `cl` is found
where Visual Studio keeps it - and every other leg on its host's own `PATH`. A tool is installed
exactly where a leg would be turned away over it: an install runs once on a host, whichever of its
legs asked first, and each leg is then told what it finds; a tool the survey could not establish
is unknown, and nothing is installed for it. `--dry-run` asks every host and installs no tool,
naming each command that would run; the survey it asks puts DssHarness on a host as `legs` does,
so a host with no .NET SDK yet has every tool unknown. `sync` creates the host's copy
of the tree it runs in and keeps it in step, deletions included: the main checkout's at the host's
`repositoryPath`, and each worktree's beside it, so worktrees do not wait for each other on a host.
The copy's git HEAD names the commit this tree is at, so a step that asks git about HEAD is answered
on a host as it is here: the commit alone by default, and every commit behind it with
`"sync": { "history": "full" }`. A clone the sync took over has its HEAD moved, detached, where it
names another commit, and no branch, tag or commit of it changed; given a commit without the one
before it, git reads it as a shallow repository from then on, which the sync says, with what undoes
it.
Deleting a worktree removes its copies from the hosts that hold one, and fails, naming it, while one
stays. A file crosses whole, either way, so one larger than 804,519,909 bytes is refused by name
before anything is sent. An
emulator counts only once its witness proves it runs programs for its processor.

`legs` runs the witness of each emulator the selected legs use, and every command that reaches
a WSL distribution or an ssh host installs or updates DssHarness there, as `config.json` declares. That is the
trust building the repository already asks for. Run `dssharness help legs` for the rules.

## Workflows

A workflow is a flow of typed steps that depend on one another by name - triage, a plan, builds,
tests, reviews, a pull request - declared by name under `workflows` in `config.json` and defined by
a yml file of its own, `.harness-config/workflows/<name>/<name>.yml`, whose directory holds every
file the definition names: its agent files, its prompts, its schemas. `init` writes one, the
implementation workflow, and declares it in the config it creates. Where `config.json` declares
`implementation`, `init` writes each of its files that is missing - the whole set where the
repository has no definition of it yet, and where it has one only the shipped files that definition
names, so a file taken out with its step stays out - and keeps each one that is there, whatever it holds, so an `init` that stopped part way is finished by the next - where a file is
missing and the definition there does not read, init refuses (12; 13 where it cannot be read), since
which files it names cannot be told; nothing that exists is ever
overwritten, and a config that does not declare it gets none of it. A config written before
workflows declares none: add `"workflows": ["implementation"]` and run `init` to have it.

A step gives a failure nothing handles to the workflow's `recover` by saying `onFailure: recover`,
itself or through a group around it: steps written once that find what went wrong and put it right.
When they are done the run goes where the recovery's `onSuccess` says - `failed-step`, the step that
gave it its failure, which runs again from where it stopped - and a failure of the recovery's own
work does what its `onFailure` says once its steps' retries are spent: `pause` or `stop` - never the recovery again, which would be it calling itself. A step
that fails again comes back by its own `onFailure`, until it passes or a person is asked; past
`maxVisits` the run asks, with the advisor's recommended fix. A reasoning step is never recovered: a
failed agent is tried again (`retries`, waiting `backoffSeconds`, twice as long each time) and on
each model of its fallback, quota or a provider that fails pauses the run where it can resume, and
what still fails is asked (`onFailure: pause`). The implementation workflow says all of it on its steps, so a run of it - once the
engine's step runners are built - ends done or asks only what it must.

An issue a reasoning step meets that was there before the run, it reports in its answer
(`preExisting`) and never fixes itself; `onPreExisting` names the step that triages each one. That
step, at the flow's top level, says `raised: preExisting` - raised once per issue, never scheduled,
as the recovery is not - and `onAddress: recover`: it reproduces the issue on the run's base commit,
gives the recovery one that is in a step's way, related to the work or critical, to fix at its root,
and lists the rest, proven, with a title and a description, in the run's `pre-existing-issues.json`,
shown in the pull request's body and the handoff. Where a workflow has such a step, every other reasoning step names it, so no issue is lost. The
engine raises the triage and keeps the list; until the reasoning step runner is built, no step reports an issue.

A reasoning step's `effort` is one level for every model it may run on, or a map giving each its own -
`all` for the rest and a model's code for one, so the shipped workflow's heaviest steps run their
Claude models at `xhigh` and GLM 5.3 at `max`, which is the highest it takes, and its other agents
every model at one level - `high`, or `low` on the `fast` tier. Each model's level is checked against
what that model takes, and `validate-workflow` says, under the line that says a definition holds,
every step's models and levels, each provider's prices for the models its steps may run on and the
date they were read, and each key a step's model would read that the slot lacks. A `test` step's
`filter` names the tests it runs; its `filterWhen` is an
expression that switches it - where it reads false the step gives no filter and every test of its
legs runs, as the shipped `verify` steps do where the run's input `affectedTests` is off, each item
tested on its own tests only where the run turns it on (`--input affectedTests=on`).

The models a step runs on are the configuration's, under `providers`: each provider is a company
whose models the tool will reach - its API's address, its keys' names, its client - and each model's
entry says what it takes, holds and costs, with the date its numbers were read. A provider's keys
are `apiKeyNames`, the names of the variables that hold them in the git-ignored slot
`.harness-config/workflows/.env/`, in the order they are tried; a second key of one company is a
second name in the list, and no key is read from the process environment - a CI machine writes the
file. `list-agent-providers` says it all without saying one key: each provider, client and tier,
for every appointed key and token `present` or `missing`, and for each setting a client reads from
the slot `present` or `absent` (none is needed), exit 0 whatever is missing; `--template` prints
the agent sections as `init` writes them into a new config, to paste into an existing one - outside
a repository too, and where the configuration it is to repair does not load. A name
declared in two of the slot's files is refused as the runner's value directories refuse one, a line
whose name is no variable's name (`[A-Z][A-Z0-9_]*`) is refused by its line number alone - it may be
part of a key pasted on its own - and a file where the slot's directory should be is refused rather
than read as an empty slot. Where a value of the slot is read - which no command of this build does
yet - a file other users can change, or can read while it holds a key, is refused with the command
that fixes it; a setting, such as `CLAUDE_EXECUTABLE`, is no key. `validate-workflow`
names every key a step's model would read that no file of the slot declares, with the steps that
would read it, and still passes - a machine that only checks a configuration needs no key. A model
reached through `claude-api` or `glm` reads its provider's keys; one reached through `claude-code`
signs in by the client's `auth` (`agentClients.claude-code.auth`): `login`, the machine's login,
where nothing is said, reading no key; `apiKey`, the provider's keys in their order; `oauthToken`
or `authToken`, the token the slot holds under the entry's `token` (`CLAUDE_CODE_OAUTH_TOKEN` or
`ANTHROPIC_AUTH_TOKEN` where it names none) - a name and never a token, appointed once beside
every provider's keys. Its `configDir` and `executable` name the variables of the slot that hold a
specific login's Claude Code directory and a program to start in place of npm's
(`CLAUDE_CONFIG_DIR` and `CLAUDE_EXECUTABLE` where they name none), appointed once the same way and
never a key. The credential slots - the runner's two
value directories and this one among them - are never handed to an agent, seeded from the main tree
or folded back into it, whatever git or the ignore block says.

The Claude Code this tool uses is the one npm installs, and no other: the tool asks npm where its
global packages are and starts the program the package names, by its full path - never a launcher,
never a batch file, and on Windows `npm.cmd` itself is never started: the tool does what that file
does and starts npm's own `npm-cli.js` with node. A run that needs a Claude model through the
`claude-code` client needs npm, and no other run does. Where npm has no Claude Code, or one older
than this tool needs, it is installed or updated with
`npm install -g @anthropic-ai/claude-code@2.1.287` - never with sudo, never past the version
needed, never beside a running session of that installation: a process that runs its program or
node running its script, a `claude` that node runs, or a `claude` whose program the machine will
not say (another user's, on Linux), each named in the refusal; a `claude` installed any other way
whose program the machine names does not count, and a process table that could be read by names
alone refuses the change, saying why. The change runs under the same machine-wide lock and the
same never-stopped, read-back sequence `update-tool` has: after a change that failed, as after
one that ran, it says what npm holds since.
This build has that step and its refusals; the engine's step runner, which takes it before a run's
first step that needs Claude Code, is still to come, so no command of this build installs or
updates Claude Code yet. A program the slot names (`CLAUDE_EXECUTABLE`, or the name the client's
`executable` gives) is a person's own choice:
started as named and never installed or updated, and a slot that cannot give it is refused, never
passed over for npm's. A client's `update` key (`agentClients.<client>.update`, true by default)
and `DISABLE_UPDATES` in the tool's own environment turn installing and updating off, and a run
that then needs one that is missing or too old will be refused with the command to run by hand.

What an agent may do is one policy, the same on every client: everything is allowed except the
root actions, the places no agent reaches, and the restrictions - which only ever refuse, from
every source at once, and never remove one another's. A restriction is one string,
`Run(git push *)`, `Write(.github/**)`, `Read(secrets/**)` or `Web(pastebin.com)`: `Run` a command
prefix (its trailing `*` any arguments, the bare command too), `Write` and `Read` path patterns as
gitignore reads them, matched against the path as it stands under each of the agent's roots, and
`Web` one domain, the domain and every name under it, or `Web(*)` the web whole. A repository
declares them under `agentPolicy.restrictions`, and a workflow's definitions and steps add their
own to them, all applied together; `agentPolicy.webDomains` is the allow list beside them (left
out, any public address), and a command that runs a program which fetches from the network -
`curl`, `wget`, `Invoke-WebRequest`, `Invoke-RestMethod`, `iwr`, `irm`, `http`, `https`,
`gh api` - is decided as a web request too, by each address it names. Refused always, not
configurable: the root actions (`sudo`,
`su`, `doas`, `pkexec`, `run0`, `gsudo`, `sudoedit`, `runas`, `psexec`, PowerShell's
`-Verb RunAs`, `wsl -u root`, `systemd-run`, `schtasks` with `/ru`, wherever a command names one),
writes outside an agent's writable roots, every read and write of a credential slot, any
command naming one or a Claude Code directory of the run's, and this tool's own verbs and
`run-side-task`, whatever any switch says. No agent starts while this tool
itself runs elevated. A shell command is read whole before it is matched - split on `;`, `&&`,
`||`, `|`, `&` and line ends, its `$(...)`, backticks, subshells and loop and branch bodies read
as commands of their own, wrappers (`env`, `nohup`, `time`, `timeout`, `nice`, `xargs`,
`command`, `builtin`, `exec`, `eval`) stripped, and it goes into `sh -c`, `cmd /c`,
`powershell -Command` and a decoded `-EncodedCommand`, to four levels - so `git -C x push` is
`git push`, and `env A=1 git push` is too. What it cannot read is said in help, not guessed at:
the inside of a script file a command runs. The worktree rules (`agentPolicy.worktreeRules`, on
unless turned off) keep an agent's worktree a thing the engine can measure: git's state-writing
verbs and `gh pr merge`, `gh workflow run` and `gh run rerun` are refused (reading git stays
allowed), git's own files are not written by a tool whatever `protectedPaths` says, an agent
does not edit its own policy or what the next launch loads (`.harness-config/config.json`,
`.harness-config/orchestrators/**`, `.harness-config/skills/**` - but for a step whose `writes`
names that directory itself - `.harness-config/runner/**`, `.claude/settings*.json`, `.mcp.json`),
and the tool's own state is written by its verbs alone, the agent's own work directory the one
exception. The protected paths (`agentPolicy.protectedPaths`, on unless turned off) are the
writes Claude Code guards - `.git/**`, `.vscode/**`, `.husky/**` and the rest of its list, which
`help agent-clients` prints and a test pins - refused on every client, so a role is not stopped
on one where it passes on another; turning them off takes this tool's refusal away, and Claude
Code's own guard stays Claude Code's. A step's `writes` is the one source that narrows: where a
step has them, a write under its worktree that matches none of their patterns is denied; the
reporter's reads are narrowed to the run's directory alone, with no command, no write and no web
at all. For Claude Code the same policy is rendered, never re-decided, into the launch's
settings: an ask rule for every tool, and the restrictions as deny rules, absolute once for each
root. This is policy, not a sandbox - a program an agent
runs opens files as the user it runs as, a file of the credential slot among them; what it is
never given is a key in its environment, and what no file tool of an agent touches is the slot
itself, in any root. Where this tool cannot decide a call it denies it, never allows: nothing
runs while the host that would answer is silent or gone. The policy is built and holds every
action behind it; the `claude-code` client asks it of every call, the API clients are what comes
next, and no command of this build runs an agent yet.

The `claude-code` client starts Claude Code once for each launch, a new session every time -
never resumed or continued, keeping no session file (one a launch leaves all the same is removed,
and said) - streamed both ways: the step's prompt is the first message on its input, never an
argument, and the launch runs in the agent's working directory with every other root it reaches
added. It is given its model and no fallback, the step's effort, turns and budget, and the tools of
the agent's capabilities and no other (`read`: `Read`, `Grep`, `Glob`; `write`: `Write`, `Edit`,
`NotebookEdit`; `run`: `Bash`; `web`: none of Claude Code's), never one that starts agents, loads
skills, asks a person or plans. Every call is asked of the engine before it runs and answered from
the policy, a denial with its rule's sentence the model reads and a line of the step's
`denials.jsonl`; an answer that does not read against the step's schema is sent back, each problem
by its path; a message the engine gives an agent at work is written to the same launch; a handoff
asked interrupts the turn and asks the agent for its handoff, no tool running in that turn; every
message is held to the model asked for; the launch's environment holds nothing of a Claude Code
session the tool was started in and no credential but the one its `auth` gives; and a key Claude
Code reports invalid is passed over, the next key given a new launch while the step has done no
work. The user's own `settings.json` in the launch's Claude Code directory and the user's
`CLAUDE.md` reach every agent on this client, but no hook of theirs runs: every launch's settings
say `disableAllHooks`, and each launch's start names that settings file and its SHA-256 and says
the hooks are off. A settings file that would give a launch a credential - `ANTHROPIC_API_KEY`,
`ANTHROPIC_AUTH_TOKEN`, `CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_BASE_URL` in its `env`, or
`apiKeyHelper` - refuses the run before any launch, naming the file; take it out, or give the
client a directory of its own (`agentClients.claude-code.configDir`). The client holds every
launch to the protocol itself: one that does not answer the host's `initialize` request within
30 s, whose init offers a tool it was not given or names a credential its `auth` did not give, or
that refuses by itself a call the engine allows never started; a call it ran that the engine was
never asked about, or refused, fails the step by the tool's name; each is ended at once. A message
no turn takes within the stall bound stalls the step; a declined request ends it `Refused`, and
an account at its limit `RateLimited`, with when the limit lifts where Claude Code says.
`help agent-clients` says it whole.

```bash
dssharness validate-workflow                 # check every declared workflow
dssharness validate-workflow implementation  # check one
dssharness list-agent-providers              # each provider, client, tier and key, present or missing
dssharness list-agent-providers --template   # the agent sections, to paste into an existing config
dssharness export-workflow implementation --shipped --to ../mine  # the shipped one, to make your own
dssharness help workflows                    # the file, every step type and every key
dssharness help agent-clients                # providers, tiers, efforts and the key slot
```

`export-workflow <workflow> --to <dir>` copies a workflow - its yml and every file it names, and
no other - into `<dir>/<workflow>/`, a folder of its own that must not be there (13 where it is):
a declared one once it holds (12 where it does not, naming each problem), byte for byte, or, with
`--shipped`, the implementation workflow as this tool ships it, which is how a version of your own
starts. A file that cannot be written takes back what was written before it, with every folder the export
made, so no folder is left with part of a workflow in it - or the refusal says why it could not.

This build checks workflow definitions - the shape of every key, every step, input, param and
output an expression, a template or a prompt names, every path typed by the schema or the built-in
output that holds it, every operator and every key held to what it takes (a `when` a boolean, a
`forEach` a list), every file the definition points at - and refuses (12) naming each
problem, without running anything, as a `config.json` that does not hold is refused. A
`config.json` that declares no workflow has nothing to check and is refused (13), saying how to
declare the implementation one. It holds a definition to the configuration too: a `run` step whose
runner, or a runner its run checks name, writes the tree (`writesTree`) names with `in` the
create-agent step whose worktree it works in; the agents a run makes, `a` and a number up to the
workflow's `maxInstances`, fit `worktrees.maxNameLength`; and a commit step gives a value for every
variable `commit.template` requires and none for one it does not declare - with no template, a
`message` and no `variables`. What only a prompt reads stands where its agent is asked it:
`{recommendations}` in a reason step's prompt and the advisor's, `{question.*}` in the advisor's
alone, `{status.*}` in the reporter's two - `{status.question}` in its `askPrompt` alone -
`{update.*}` in the prompts of the step that plans an update and of the steps of its scope, and
`{piece.*}` in a step of the work-item group; the advisor's and the reporter's prompts read no step
by name, and a pull request's `body`, which the engine fills and no agent reads, reads what a
template key reads and no skill. A question gives only the texts the engine names (`step`, `kind`,
`problem`, `answers`, `record`), and so do the status (`facts`, `question`, `records`), a plan
update (`cause`, `since`, `work`) and a piece (`kind`, `changed`, `keep`, `undo`, `instead`,
`cause`, `before`); the bar is one text, and so is each run fact (`{worktree}`, `{os}` and the
rest), which a template reads and an expression does not. The engine starts and drives a run of a workflow
(`run-orchestrator`, `dssharness help orchestrators`), from the main checkout's HEAD on a branch of its own: a start is
refused where HEAD is detached, where the checkout is on the branch the run's pull request goes to, where a merge is under
way and where changes are staged. Sixteen steps are served so far: `orient`, which reads what the repository is and has once
(its branch and the pull request's base branch, the commit the run started on, a remote, legs and how each tests, anchors,
CI, mutation arms, what is dirty, the open agents and who holds the tree); `graphify-codebase`, which syncs the run's one map
of the repository - every file a node, each declaration a language's implementation reads with its lines, its header and
the first sentence the code writes of itself (C# through the compiler's own parser, MSBuild's projects and solutions,
Markdown's headings; every other file by its name and size), and the edges between them, each saying how sure it is - under the run's
`graph/`, reading again only the files whose content, reader, reader's version, size limit or languages changed and those it could not read, and starting a run's first
graph from the newest earlier run's (`dssharness help codebase-graph`; the workflow's `codebaseGraph` shapes it);
`validate-plan`, the machine's check of a plan's
work items; `create-agent`, which makes the agent the engine named for it (`a<N>`, never twice) for the model its
first reason step runs on, both recorded before the agent is made so a step run again asks for the same one; and the leg
steps `build`, `test`, `run` and `check-mutations`, which run their legs as the commands do, in the worktree of the agent
they name with `in` or in the main checkout, each leg's verdict read on its own and its red legs answered alone - a leg
that failed with the phase that did not pass and its last lines, a sweep's arm with its arm - a leg in a copy letting the
tree go once its copy is made, and keep the ledger, and in an agent's worktree each red leg's failing log, in the step's
directory; the checks `anchor-balance` and `anchor-citations`, each held to the rows or files a step names; `ci-read`,
the legs `check-ci-legs` reads; and `ci-wait`, which waits for every check the forge runs on a commit - the one the run
last pushed where it names none - ends at the first red one with its job's failing step and whole log, and asks where
none appeared, some are still running, or one ended neither green nor red; and the fold family - `fold`, `delete-agent`,
`refresh` and `rebase` - which make their verb's dry run first, read what it stood, typed, and ask what it lacks - a
consent, a path outside a fold's `within`, conflicting merges - with what it is about, apply only what it named, write
each path a fold means to write to the run's ledger of its folds (`folds.json`) before the verb and what it left after,
and, of `others`, work on each other open agent of the run in turn. A check that finds red fails its step under
`failOn: red`, every check's default but `ci-read`'s. Every text a step hands on is filled where it starts, and one that
reads nothing fails the step (12). Every call of `gh` is started so it cannot ask, and ended after five minutes or the
step's `timeoutMinutes` (21). A step of any other type fails, saying no runner serves its type; the rest are the next
thing the tool grows. A step that works in
or on an agent waits for the create-agent step that makes it. Run `dssharness help workflows` for the whole of it.

## Anchors

An anchor is a named piece of deferred work, kept as a row in a markdown registry so it
cannot be forgotten. Two registries hold every anchor: pending, for open, gated and
disclosed anchors, and done, the archive of closed ones. Their paths are set in
`config.json` under `anchors`, and `init` creates each one that is missing.

```bash
dssharness write-anchor D-AUTH-TOKEN-REFRESH --priority P1 --trigger "tokens expire mid-request"
dssharness set-anchor D-AUTH-TOKEN-REFRESH --status closed   # moves it to the done registry
dssharness read-anchor D-AUTH-TOKEN-REFRESH
dssharness read-anchors --open --band P0 P1
dssharness check-anchor-balance --base main
```

The Status cell (`🟠 OPEN`, `⏳ GATED`, `🔵 DISCLOSED`, `✅ CLOSED`) is the only verdict that
decides whether a row is closed. Closing an anchor moves its row to the done registry, and
`check-anchor-balance` fails a change that creates more anchors than its work closes. A row is
one line, so a value's line breaks are stored as spaces: a value that would be stored with an
id or a path cut in two is refused, and so is one that newly cites an id no row holds. Run
`dssharness help anchors` for the rules.

## Building from source

```bash
dotnet build DssHarness.slnx
dotnet test --project tests/dss-harness-test/dss-harness-test.csproj
```

Everything a build produces — binaries, intermediates and packages — lands under
`build/`, which git ignores. Deleting it is a complete clean.

The end-to-end tests build real CMake projects, so they need `cmake`, `ninja` and a C and C++
compiler on the `PATH` (`gcc` and `g++` on Windows, `cc` and `c++` elsewhere), and on Windows
one of them needs Visual Studio's C++ build tools. A machine lacking any of these skips those
tests, naming what it lacks; set `DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS=1`, as the continuous
integration runners do, to have them fail instead.

## Documentation

- [Architecture](docs/architecture.md) — layering, paths, hosts and legs, parallel execution, contamination guarantees
- [Releasing](docs/releasing.md) — channels, trusted publishing, the release pipelines

## License

DssHarness is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE)
and [NOTICE](NOTICE).
