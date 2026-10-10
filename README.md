# DssHarness

One cross-platform CLI for a repository's worktrees, builds, tests and cross-host
work — configured entirely from a file in the repository, not compiled into the tool.

```bash
dotnet tool install --global dssharness
dssharness --help
```

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
| `delete-worktree <name> [--force]` | Remove a worktree and everything under it, its copies on hosts, and the mutation workers kept beside each; refuses one holding work that would be lost, a locked one, one whose evidence directories hold measurements, or one with a mutation worker beside it that a sweep still running holds or that cannot be removed, without `--force` |
| `list-worktree [--hosts] [--json]` | List existing worktrees with the commit each was made from, the copies hosts keep of them, and the copies left by worktrees that are gone; `--hosts` also asks each host what it keeps, and how large each copy is |
| `create-orchestrator <o> --model <id> [--parallel N]` | Create an orchestrator under `.orchestrators`; `--parallel` (4 unless given) is the most agents with a worktree at once |
| `create-agent <o> <a> --model <id> [--empty]` | Create an agent: its record, its worktree at `<worktrees.root>/<o>/<a>`, and its seed, the main tree's uncommitted state handed to it |
| `seed-agent <o> <a> [--empty] [--force]` | Seed a live agent again, with every path the main tree holds otherwise than the agent shares it, committed or not; refused over changes of its own without `--force` |
| `refresh-agent <o> <a> [<path>... \| --all] [--except <path>]... [--apply]` | Copy into a live agent every path under the paths - the anchor registries' directory by default, anywhere in the tree with `--all` - that the main tree holds otherwise than the agent shares it, committed or not, recorded as handed to it; refused over a path the agent changed, naming each, unless `--except` leaves it as the agent changed it |
| `rebase-agent <o> <a> [--apply] [--settled <path>]` | Move a live agent's base to the main tree's HEAD: what the main tree committed since comes in as git holds it, its own changes and what it was handed stay; refused over a path both changed unless `--settled`, and over anything of its own what comes in would go over; run again after one that stopped part way, it finishes it |
| `fold-agent <o> <a> [--apply] [--settled <path>] [--new <ID>] [--accept-lost <ID>:<cell>]` | Fold an agent's own work into the main tree and apply the rows it filed, every refusal named before anything is written and the rows all or nothing; a row is made only where `--new` names it, and a cell that does not keep its stored text written only where `--accept-lost` names it; its worktree is kept |
| `delete-agent <o> <a> [--apply] [--settled <path>] [--new <ID>] [--accept-lost <ID>:<cell>] [--discard-uncommitted]` | Fold what is left and apply its rows, keep its evidence and transcripts, then remove its worktree and its copies on hosts |
| `list-orchestrator [<o>] [--json]` | List orchestrators, their agents and where each stands |
| `delete-orchestrator <o> [--delete-evidence]` | Delete an orchestrator once every agent of it is deleted |
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
| `sync` | Put a host's copy of this tree in step with it, deletions included: each worktree has a copy of its own |
| `build [--legs a,b] [--time]` | Build every selected leg, in its own variant-keyed build directory |
| `test [--legs a,b] [--time]` | Build and test every selected leg, with a witness for each verdict |
| `run <runner> [--legs a,b] [--time] [--input name=value]` | Run a predefined runner across the legs it declares, giving its action's inputs values for this run. A run line names what only the tool knows of a leg - its build directory, the file its build makes, and the compiler its build identified, `{compiler_C}` or `{compiler_CXX}`, which is gcc on one leg and cl on another - and a step naming one of those has its leg built first (`help runners`) |
| `check-mutations [--legs a,b] [--arms a,b] [--self-test]` | Prove each selected leg's tests can fail: in worker copies of its tree, mutate each arm the registry declares, build it, witness every object depending on the site rebuilt, run its test binary whole and judge what reddened; `--self-test` sweeps the fixture the tool carries instead, through each leg's toolchain (`help mutations`) |
| `clean [--legs a,b] [--dry-run]` | Remove each selected leg's build directory, and the mutation workers its sweeps and self-tests keep beside its tree, wherever the leg runs, so a full disk can be freed (`help space`) |
| `host-exec --ssh <name> \| --wsl [<distro>] -- <command>` | Run a DssHarness command on an ssh host or in a WSL distribution |
| `help [topic]` | Explain exit codes, configuration, legs, disk space, heavy-leg admission, worktrees, orchestrators, anchors, layout, secrets, tools, runners, verdicts, mutation testing and CI legs |

Every command takes `-C, --directory <dir>` and `-v, --verbose`.

Zero means every selected leg reached a verdict and none failed. A run where nothing failed but
some leg never reported exits `21` and names those legs: a leg that did no work proves nothing
about the code, so it is never counted among the legs that passed. A command that changes an
orchestrator or an agent exits `21` too when it stops part way - an agent closed and its worktree
not yet removed, a fold or a hand-over half written, a move of an agent's base half made, an
orchestrator half deleted; run again, it finishes.
`dssharness help orchestrators` explains seeding, rebasing, folding and deleting agents.

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
operating system is observed in two tightly scoped places and nowhere else;
everything downstream is platform agnostic. CI runs the whole suite on all three, on
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
.orchestrators/<o>/agents/<agent>/           ignored; an agent's record, seed, rows, the rows applied, kept evidence
.harness-config/runs/                        ignored; one directory of logs per run
.harness-config/lock.json                    ignored; records in-progress runs
.plans/_deferred-anchor-registry.md          tracked; live anchors
.plans/_deferred-anchor-registry-done.md     tracked; closed anchors
```

The worktrees root is `worktrees.root` in `config.json`, shown here as `init` writes it. A
configuration that names none keeps worktrees under `.harness-config/worktrees`, which spends
15 more characters of the path budget before a worktree's own name.

Ignored state lives only in the main checkout. A worktree receives the tracked part
of `.harness-config` through git but never the ignored part, so secrets and the run
lock resolve back to the originating checkout.

## Legs and hosts

A leg says what it needs, never where it runs: an operating system, a processor, and
optionally the emulator it runs through, which rules out running it natively. Hosts are
measured before anything starts: this machine first, then WSL distributions and ssh hosts
only for the legs this machine cannot take, and a WSL distribution only for a Linux leg.
Each leg runs on the first host that can take it - its operating system, its processor and
its emulator - and is turned away there when that host lacks a program its command requires.

```json
{
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
    "mac-x64-release": { "os": "macos", "processor": "x86_64", "emulator": "rosetta", "config": "release" }
  }
}
```

```bash
dssharness legs                                       # every leg: where it runs, or why it cannot
dssharness legs --legs linux-release,mac-x64-release
dssharness host-exec --ssh mac-mini -- verify-git
```

A host's section can also give its own `buildCores` and `testCores`, and an `env` that every
process a leg starts there sees - each build phase, the test runner, each step of a runner - as
the lowest layer, beneath the variant's, the test invocation's and the runner's own. A `PATH`
set there is where that host finds those programs, so none of them is required of it before a
leg starts: each is the run's to find - except in a developer environment, whose `PATH`, built
over the host's, is looked in before the leg starts.

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
`msvc` is never reported missing on a MinGW leg of the same machine. Each leg is told about a tool
as it will find it: a leg whose toolchain names a developer environment, on the `PATH` that
environment sets up for its processor - set up here for the look, so `cl` is found where Visual
Studio keeps it - and every other leg on its host's own `PATH`. An install runs once on a host,
whichever of its legs asked first, and each leg is then told what it finds. On another host, a
tool its own `PATH` lacks is unknown for a leg in a developer environment, since this command
sets one up only on the machine it runs on, and nothing is installed for it. `--dry-run` asks
every host and installs nothing, naming each command that would run. `sync` creates the host's copy
of the tree it runs in and keeps it in step, deletions included: the main checkout's at the host's
`repositoryPath`, and each worktree's beside it, so worktrees do not wait for each other on a host.
Deleting a worktree removes its copies from the hosts that hold one, and fails, naming it, while one
stays. A file crosses whole, either way, so one larger than 804,519,909 bytes is refused by name
before anything is sent. An
emulator counts only once its witness proves it runs programs for its processor.

`legs` runs the witness of each emulator the selected legs use, and both commands install
or update DssHarness on the hosts they reach, as `config.json` declares. That is the
trust building the repository already asks for. Run `dssharness help legs` for the rules.

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
dotnet build RepoHarness.slnx
dotnet test --project tests/repo-harness-test/repo-harness-test.csproj
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

repo-harness is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE)
and [NOTICE](NOTICE).
