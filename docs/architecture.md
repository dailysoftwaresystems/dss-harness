# DssHarness architecture

## Why this exists

A repository's build, test and cross-host work is usually a pile of paired
`.sh`/`.ps1` scripts that drift apart, encode one repository's facts, and fail
differently on each platform. `DssHarness` replaces that with one
cross-platform .NET tool whose behaviour is driven entirely by `config.json`.

**Nothing about any specific repository, language or toolchain is compiled in.**
If a behaviour cannot be expressed in `config.json`, that is a defect.

## Status

Implemented today: `init`, `verify-git`, `create-worktree`, `delete-worktree`,
`list-worktree`, the orchestrator commands (`create-orchestrator`, `delete-orchestrator`,
`list-orchestrator`, `create-agent`, `seed-agent`, `refresh-agent`, `rebase-agent`,
`fold-agent`, `delete-agent`), the run commands (`run-orchestrator`, `resume-orchestrator`,
`answer-orchestrator`, `pause-orchestrator`, `stop-orchestrator`; the steps `FlowStepRunners.Served` lists are served - `orient`, `graphify-codebase`, `validate-plan`, `create-agent`, `build`, `test`, `run`, `check-mutations`, `anchor-balance`, `anchor-citations`, `ci-read`, `ci-wait`, `fold`, `delete-agent`, `refresh`, `rebase` - and a step of any other type fails until its runner lands), `validate-workflow`, `export-workflow`, `list-agent-providers`, `check-root-litter`, the anchor commands (`write-anchor`, `set-anchor`,
`read-anchor`, `read-anchors`, `check-anchor-balance`, `check-anchor-citations`),
`fix-line-endings`, `check-ci-legs`, `legs`, `host-exec`, `install-missing-tools`,
`update-tool`, `sync`, `build`, `test`, `run`, `check-mutations`, `stop-run`, `clean`, `up`, `down` and
`help`.

Every section of this document now describes code that exists. Where a rule is stated in
the present tense it is enforced, and a gap between the two is a defect in the tool rather
than a section still waiting to be written.

## Layering

```
DssHarness.Cli        Program.cs and Commands/: argument parsing, the console and the exit code only
DssHarness.Core       domain, services, abstractions; the lifecycle every command shares
                      (Application), the registrations (Composition) and the help's text (Help)
dss-harness-test      tests

(DssHarness.Adapters was planned for the build adapters; they arrived in
DssHarness.Core instead, beside the build service that is their only caller, and the
project was not created.)
```

`Core` is a library, so "no logic in Program.cs" is enforced by the assembly
boundary rather than by discipline. The registrations are the core's own
(`ServiceRegistration.AddDssHarness`), so a front end other than the command line builds the
same services; the core builds each operation's provider (`ServiceRegistration.Host`), and the command line supplies only how a
password is asked at its terminal (`ConsoleHost`).

### The platform layer

`HostPlatform`, `FilePermissionsFactory` and `ProcessTableFactory` are where the tool learns
which operating system it runs on and builds what differs by it. Everything above them depends
on `IHostPlatform`, `IFilePermissions`, `IProcessTable`, `IProcessRunner` and `IFileSystem`.
Below that seam a few primitives ask the runtime for one fact each, where nothing else is
asked - `PhysicalFileSystem`, `ProcessRunner`, `DetachedProcessLauncher`, `Containment` (the
session, group, signal and job-object calls containment is made of, and the choice between them
that `ContainExec` places a child through), `UserState`,
`WslDiskImages`, `CMakeToolchainReader`, `ProcessContext` (the process's current directory) and the
command that holds a host awake.

A file is read through one stream (`IFileSystem.OpenRead`) and written whole by one of two atomic writes, each writing
a temporary file beside the target and renaming it over the target: `IFileSystem.WriteAtomic`, handed a stream, for
what is made as it is written - a record, a text - and `WriteAllBytesAtomicAsync` for bytes already in hand, which a
tree sync, a fold's copy and a mutation write, waiting between its attempts without holding a thread. A whole text or
a whole file's bytes is read and a text written by a convenience over those (`WholeFiles`), so a double that changes
how a file reads or writes changes it for every caller, whatever form the caller takes it in. A state file
(`JsonStateFile.Read`, `JsonStateFile.Write`) is parsed straight off that stream and serialized straight onto it,
never held as one text: a codebase graph's link result runs to tens of megabytes, and held as text it costs twice its
size again, in buffers the runtime's shared pool keeps. A file is renamed into place as POSIX renames, on Windows too
(`Win32Files.TryReplace`): one who holds the old file open - every read here shares its deletion - goes on reading it
while the new one takes its name, where Windows' own rename is refused while the old file is open at all, so a writer
could not replace a record another sync was streaming. A volume that cannot rename so - FAT, some shares - is renamed
Windows' own way.

A platform-specific implementation is created only where behaviour genuinely
differs. Today that is three families of behaviour.

**File permissions**: making a file readable only by its owner, deciding whether a file
is a program at all, and whether other users can read or change a file. Unix answers
these with mode bits; Windows answers them with access lists and file extensions. Hence
a POSIX implementation and a Windows implementation, and no third.

**The process table**: what else is running on this machine, with each process's parent,
its start time and its command line, which is what a leg's contention check reads, and the
full path of the program it runs, which is how an update tells a session of the installation
it would replace from a program of the same name installed elsewhere - a command line need
not name its program by a path at all, and a process may write its own name and line over, as
node running Claude Code calls itself `claude` on Linux and macOS.
Windows publishes it through WMI, Linux through `/proc`, macOS through `ps` - whose name for a
process is the first word it was started with, never its program - and libproc's
`proc_pidpath`, which names the program. The kernel's own answers (`IProcessKernel`: `/proc`'s
files, libproc's path) sit behind a seam of their own, so a test scripts the ones a machine
cannot be made to give - another user's program, which Linux keeps from this user, a process
that ended, a zombie - and the table carries each as none. Behind the
seam so that nothing above it branches on the operating system to find out, and so a test
can say what the machine was running without the machine having to be running it.

**The input watch**: what a leg's work hears change among its inputs while it runs. On
Linux every watch of a process goes through one inotify instance the process calls itself
(`InotifyHub`); elsewhere, and on a Linux whose C library does not give those calls, through
.NET's watcher (`FileSystemWatcherKind`), each watched directory then taking an instance of
its own on Linux. `WatchKinds` chooses, in one place, from `IHostPlatform`.

### Process execution

`IProcessRunner` takes arguments as a **list**, never a command line string.
Shell quoting is therefore not part of the system. This is not a style
preference: a neighbouring repository measured a shell-string invocation whose
empty variable expanded into `rsync -a --delete / /`, ran for 70 minutes and
reported exit 0.

Child output is decoded, and child input encoded, as UTF-8 on every platform. Left to
its default, Windows decodes redirected output with the console's legacy code page, so
a path such as `C:\Users\João` printed by git would reach the harness garbled on
Windows alone.

A missing working directory is reported as exactly that. Linux and macOS report it
with the same error number as a missing executable, which would otherwise surface as
"git is not installed".

A caller's handler for the lines a child writes is handed no more once it fails, and the
stream is still read to its end, what the handler raised being raised once the child has
gone. Left to stop reading, a child writing more than a pipe holds blocks on it, and a
host's agent - whose input is held open, so that it can tell the machine that asked has
gone - never ends: a file read back from a host whose content was refused part way left
its command waiting until something stopped it.

A program named without a path is looked up in the `PATH` directories and nowhere else -
the `PATH` the child is given, which ends with the directories the survey found a leg's
programs in, and begins with any a tool's `minVersion` raised. Left to the runtime, it would
be looked for beside the running executable and in the current directory first, and the
current directory is usually the repository, so a file committed there under a tool's name
would run in place of the tool. On Windows a name
without an extension starts only `<name>.exe`, never a batch file, whose arguments cmd.exe
would parse a second time, and a path without one starts that path with `.exe` added. A
program a leg's configuration names by a relative path is read from the directory its phase
starts in - a step's working directory, the test runner's - never from wherever the harness was
started: for a leg on a worktree those are different copies of the same file.

A request can ask for a child **contained**: what it started ends with it, at its own end as at a
stop, and a signal to the harness does not reach it. A process has to be where it is contained
before it runs a line of its own, and `Process.Start` offers no moment between a child's making
and its start on any system, so the runner - the one owner of processes - starts a contained child
through a copy of this tool that puts itself there first: the hidden `contain-exec` command, which
on Linux and macOS makes itself the leader of a new session and process group (`setsid`) and then
becomes the program by its whole path (`execv`, under the same process id - never `execvp`, which
runs a file with no interpreter line under `/bin/sh` where any other start refuses it), and on
Windows joins the named job
object the runner made and starts the program in a process group of its own, handing the streams
it was given on to it. On Windows the job is ended as the child ends or is stopped, and every
member goes with it; and since its members also end when its last handle closes, what a killed
engine leaves behind ends with it there. On Linux and macOS the runner ends the child's session as
the child ends or is stopped - every process still in it, and at a stop every one still in the
child's tree, read before anything is signalled, asked to end with SIGTERM, and whatever still
stands five seconds later killed - and the output is read for five seconds more at most: a process
that made a session of its own away from the child's tree is not reached, and nothing waits for
what it holds. A process table that cannot be read - macOS's is read through `ps`, which a session
that has used up every process it may start leaves unable to start - takes nothing from the stop:
the group is still asked and then killed by its id, which needs no table, the tree is still ended,
and what went unreached is said in the result (`ProcessResult.Uncontained`), or, where the caller
gave up, carried by the cancellation it is thrown (`ProcessStoppedException.Uncontained`) and said
in its message. A process killed there - a crash, `kill -9` - ends nothing
of the sessions it started, so each one a step of an orchestrator starts is recorded while it runs,
by the runner through the tool's own records: once the child is placed in its session and before
it is said to have started, `<step>/children/<session>.json` in the step's own directory - its
session (the child's process id), the child's start stamp - said by `contain-exec` of its own start
on the channel, just before it becomes the program, which keeps it, so a child that ends at once
is recorded all the same - its program, and the process that
started it with that process's machine and stamp (`ChildSessions`, the request's
`SessionRecord`) - and taken back once the runner has ended the session. The same record, with
`group: true`, names the process group a run's machine step's legs' work leads (below: a bound child
under a `ProcessScope`), taken back once the child has ended and nothing is left in its group. A
record that cannot be
written - one whose child's start nothing can read among them - is a start that failed: the child
is ended, and never said to have started; a defect that keeps one from being written, a record that
would not read back among them, ends the child too, and is raised as it is. Every command
over an orchestrator - `create-`, `list-` and `delete-orchestrator`, each agent verb, and each run
verb over a run already made - first
sweeps its records, before it reads anything else of it: a session or group whose starter still stands is
its starter's and is left; one whose starter has gone is ended as the runner ends a contained
child's session at its end - a group by its own members and the tree its leader stands over, never
the session it shares with its starter (`Containment.EndSessionAsync`, `group`) -
its record removed and the end said in the command's lines and in its document's `ended` where it
answers with one, where anything was left in it - but only where the process carrying its id still
has the recorded start, or no process carries the id at all: none is given an id a session or a
group still carries, so what a leader killed with the engine's tree handed on is still found by
it. An id given to a later process ends nothing, and that record simply goes.
`stop-orchestrator --now` sweeps again once it has ended the engine and its tree, and says what it
ended: what outlived that end. A tree is ended by stopping each process before killing it, and the
system hangs up a group that loses its last parent in the session while one of its members is
stopped, so a leftover may die of that hangup with the engine, and is then not said. A
record that does not read as one is said and left, and a starter on another machine is never taken
to have gone. Windows records nothing: its job already ends a killed starter's children.

A request can ask instead for a child **bound** to the harness's life (`ProcessRequest.Bound`):
on Windows it is placed through `contain-exec` too, in the one job object the process holds for its
whole life - whichever operation's runner starts it, though each operation is given services of its
own - which ends every member when its last handle closes; so the harness ending in any way, a hard
kill among them, ends
the child and all it started at once. Its own end and a stop are as any other child's: nothing is
terminated as it ends, and what it leaves running lives on until the harness ends. Every phase of
a leg a dispatcher runs on its own machine - a build, a test, a runner's step, a mutation arm's
run, a set-up's down line - is bound, and so is its `keepAwake` command: a dispatcher killed where
it stood once left its legs' test runners running for minutes, each holding a heavy leg's room. A
set-up's `up` line is not: what it brings up is shared, held across runs, and ended by its `down`
line. A child held in a job - contained or bound - is given build servers of its own, where its
environment names none: the compiler's program-database server listens at an endpoint named for
the job (`_MSPDBSRV_ENDPOINT_`), and the build engine leaves no node for a later build
(`MSBUILDDISABLENODEREUSE=1`) - a server a member starts ends with the job, and one shared with
another dispatcher's builds would be taken from them mid-build. An endpoint named for a job of the
harness's own is replaced too: a harness a bound phase of another started inherits that one's, and
a server its own children started there would end with its job while the outer builds still used
it; an endpoint the user named is kept. The ssh or wsl.exe that carries a leg to a host is bound
too (`HostCommand.Bound`): the host's leg ends when its input does. On Linux
and macOS a bound child starts as any other: an interrupt or a termination signal given to the
harness ends it with the harness's stop, a hang-up of its terminal reaches it as it reaches the
harness, and a kill of the harness alone does not reach it - `stop-run --now` ends the harness with
its whole tree. Save under a **process scope** (`ProcessScope`), which a run's machine step opens
around its legs (`LegStepRunner`), flowing with the operation and seen by nothing outside it: there
every child but a contained one is started apart from the terminal, so the engine's first Ctrl+C -
a safe pause - leaves the builds running, and a bound child's group is recorded in the step's
directory as a contained child's session is, while anything is in it, so a killed engine leaves
none of it unrecorded; a stop ends the group at once with the tree. On Windows a child keeps the
console of its own it always has, the job binds a bound one, and nothing is recorded.

Whether the program started is said on a channel of the runner's own (`--status`), never in the
program's streams or an exit code a program could give too: a program that cannot start is
reported as one that never ran (`ExecutableNotFoundException`, `ProgramStartException`, worded as
for any other start, a program being written tried again as any start is), and the child is said
to have started (`OnStarted`) only once the step has placed it. The group alone, apart from the
terminal but within the session, is the lesser level a request asks for by name
(`ApartFromTerminal`), and only Linux and macOS start a child through the step for it: an
interruption typed at the harness's terminal is sent to a group, and that group is not the child's.
On Windows such a child starts as any other, since a child started with no window already has a
console of its own that an interruption typed at the harness's never reaches. A child apart from
the terminal is stopped by its process tree, as any other, and on Linux and macOS by its group as
well, at once, which reaches what its tree handed on; what it leaves at its own end is left. No leg is contained: a `build`, `test` or `run` typed by hand starts its legs' phases bound on
Windows, as above, and as any other child elsewhere.

### Paths come from git

Every repository path the harness holds — the tree a command acts on, the main
checkout, each worktree — comes from git, in the single form git resolves it to, with
symbolic links followed. A directory the user typed is only ever used to tell git where
to start. If one root were spelled as typed and another as git resolved it, the same
checkout would compare unequal to itself wherever a link is involved: macOS keeps its
temporary directory under the `/var` link, and any repository cloned beneath a linked
directory behaves the same way.

`--directory` is resolved to an absolute path, and confirmed to exist, once, before any
command runs.

git's messages are matched in two places only: telling "not a repository" apart from
"git could not look", and "not a gitdir" apart from the same when a directory among a
worktree's submodule repositories is examined. Those read-only queries run with
`LC_ALL=C`, so a translated git still answers in the words being matched. Every other git
command, including those that run the user's hooks, keeps the user's locale.

### Reading config.json

The file is edited by hand, so it is checked as it is read, and every problem is reported
at once, with the line it concerns where the parser knows it:

- An unknown key is an error. A misspelled setting that is silently dropped reverts to
  its default while the file plainly appears to set it.
- A key written twice is an error, in any case: read, the file kept the last copy's value and
  dropped the first's without a word.
- A member the reader never reads, one marked `[JsonIgnore]`, is no key of the file's: a key naming
  it is refused like any other. Left to the serializer, its value is skipped without a word.
- `null` is refused wherever the model does not allow it, including inside lists and
  maps, which the serializer does not check on its own. Loaded, it would fail much later
  as a crash in whatever first read it.
- A list whose absence means every one of what it names, or a set the tool chooses, is refused
  given empty, by one rule and in one wording: a runner's `legs` and `steps`; a tool's `platforms`,
  `toolchains`, `legs`, `processors` and `emulators`; a toolchain's `platforms`; an emulator's
  `phases`; `ci.workflows`; `test.inputs`; a project's `targets` and `rebuildableFormats`; a
  platform's `toolSearchDirectories`. Read as the key left out, a runner's `"legs": []` synced the
  tree to every host and ran on every leg, for a list naming none, as `--legs` given no name would
  have. Left out, each still means what it did, and a list a caller builds empty in code reads as
  left out. A list whose empty form means none - such as `sync.neverTransfer`, `sync.exclude` or
  `contention.buildTools` - takes `[]` as that.
- A key nothing reads is refused, saying so: a test section's `configs`, which `init` once wrote
  and nothing ever read - each leg builds and tests the one config its `config` names - and `exec`,
  whose named commands no command ever ran: each is a set-up's line under `setUp` now, saying so.
  Refused even empty, since the file plainly appears to declare commands.
- References are resolved: a leg naming an undeclared host or emulator, an emulator that
  runs programs for another processor than the leg's, a success pattern that does not
  compile, a commit template placeholder no variable declares.
- **A toolchain CMake builds with names its compiler**: `CC` or `CXX` under `env`,
  `CMAKE_C_COMPILER`, `CMAKE_CXX_COMPILER` or `CMAKE_TOOLCHAIN_FILE` under `cacheVars`, or a
  `compilerId` CMake must configure it with. One naming none is refused, because CMake then takes
  whatever compiler it finds first - how a leg named `msvc` built with MinGW's gcc on every run until
  `CC` was declared - and the build directory guard has nothing to hold a later build to. A
  toolchain only .NET or Dart projects build with names none: those resolve their own compilers,
  and its `cacheVars` are that build's properties. `init`'s `msvc` toolchain names `cl`.
- **Every CMake configure is asked which compilers it resolved**, through the file API: the build
  writes the `toolchains-v1` query into its build directory before configuring, notes the answers
  already there, and reads the one that configure wrote. Each leg's line names what CMake answered -
  `compiler: MSVC 19.51.36231 (C, CXX)`, one entry per compiler with the languages it serves -
  whatever the verdict, and `--json` carries it as `compilers`. A configure that fails answers
  nothing about the compilers - CMake 4 writes an error index in its place, and leaves the last
  successful configure's answer where it was - so it names none, never the ones an earlier
  configure resolved; told apart by what was there before, never by the times in the names, which
  a clock that stepped back would reorder. It travels beside the detail rather than inside it, so a
  leg a host ran is named once on the machine that reports it. A toolchain's `compilerId` -
  `{"C": "MSVC", "CXX": "MSVC"}`, in CMake's own ids - holds the build to it: a compiler CMake
  configured that contradicts it fails the leg before anything is built, and a declared language
  CMake identified no compiler for leaves it `unwitnessed`, naming why - an older CMake writes no
  answer, and a misspelled language is never answered. The answer holds what the top-level
  directory holds, so a language only a subdirectory enables - C, where a C++ project fetches
  googletest, whose own `project()` declares C and C++ - comes with no id: with its compiler's
  path where CMake caches one, and under a Visual Studio generator, which caches none, with no
  path either. A leg declaring the C it built with was left `unwitnessed` while its configure log
  named that compiler. CMake keeps each language's identification once for the whole build
  directory, in `CMakeFiles/<version>/CMake<language>Compiler.cmake`, which enabling the language
  in any directory loads, and the language is identified from that record. The record is held to
  the answer, because it can be a later configure's: one that identifies the compiler again -
  given another, or run with `--fresh` - rewrites it, and one that then fails writes no answer,
  which leaves the last one's beside a record of a compiler that built nothing there. Read that
  way, `test --no-build` named a compiler its binaries were not built with. So the record must name
  the compiler the answer names, where the answer names one; a record nothing ties to the answer
  identifies nothing, and the language is `unwitnessed`, with why. The answer keeps a toolchain
  file's value as written, where the record holds what CMake's
  `Modules/CMakeDetermineCompiler.cmake` made of it, so the two are compared as that: a list's
  first item - `gcc.exe;-m64` names `gcc.exe` - a path tidied of `.`, `..` and doubled separators,
  with forward slashes, and a name alone found as a program, with `.com` or `.exe` after it on
  Windows. Measured with CMake 3.29 and 4.3: MSVC through Ninja and through Visual Studio 18 2026,
  gcc on Windows and on Linux, toolchain files naming the compiler each of those ways, and a
  configure that failed after identifying the compiler again. Never by when either was written: a
  host whose clock steps forward for a moment stamps a file ahead of one written after it - on a
  consumer's WSL host, two files written a fraction of a second apart came out 24 seconds apart, and
  a configure's own record came out dated after its answer, failing a leg as `unwitnessed` - and a
  phase that starts and ends outside the step measures no drift. Read for the configure that wrote
  it, the record can be no later configure's: only what that configure answered is read, and nothing
  configures between it and the reading. Read for a directory a leg did not configure, a configure
  since is told apart by what CMake leaves of it: one that identified another program by the
  program the record names, and one that failed, whatever it identified, by the error index CMake 4
  writes in place of an answer - kept beside the last answer, where a configure that answers
  removes every one before it, measured with CMake 4.3. Before CMake 4 a configure that failed
  leaves nothing of itself there, and the record is read as it names it. A language
  CMake identified nowhere, such as the resource compiler it lists on Windows, is left out of the
  compilers, and a toolchain declaring one is told what CMake's answer named for it and why that
  is no id, never that CMake named none. `test --no-build` names what its build
  directory was last configured with, and holds it to the same `compilerId`: binaries a compiler
  nobody chose produced are failed rather than tested. A runner that does not build names none.
- **A compiler updated in place starts its build directory from clean.** CMake identifies a cached
  compiler once, when a build directory is first configured, and loads that record on every
  configure after; a build system has no edge on the compiler itself. A Visual Studio update
  rewrote `cl.exe`, `c1xx.dll` and `c2.dll` in the same toolset directory, taking `cl` from
  19.51.36257 to 19.51.36260, and a consumer's trees configured before it still recorded the old
  version: the first build of each failed every precompiled header with C1853, "from a different
  version of the compiler". So before each build of a CMake project, the C and C++ compilers the
  directory's records name - under the version of CMake that last answered there - are asked their
  versions as CMake identified them: a line naming the macros CMake's identification reads is
  preprocessed, in the leg's own environment, and the values put together by CMake's formula for the
  id it recorded - `_MSC_VER`, `_MSC_FULL_VER` and `_MSC_BUILD` for MSVC, `__GNUC__` with its minor and
  patch level for GNU, `__clang_major__` and its fellows for Clang, with `__apple_build_version__` for
  AppleClang. Never by which are defined: clang defines `__GNUC__` too, as 4.2.1. A version that
  differs, a compiler that is not there, or one that defines none of its id's macros starts the
  directory from clean, naming both versions. Preprocessing takes a fraction of a second, where a
  scratch configure took 19 with MSVC; measured against CMake 4.3's records, MSVC 19.51.36260.0
  through Visual Studio 18, MinGW gcc 13.2.0, Linux gcc 13.3.0 and clang 18.1.3 each came out as CMake
  wrote it. A compiler that cannot be asked is said and passed over - the question names the cause of
  a failure the build would show anyway - and one of another id is not asked.
- **A toolchain may name a developer environment**, declared once under `developerEnvironments`,
  and one naming none that is declared is refused, as is a `visualStudio` one on a toolchain whose
  `platforms` is not `["windows"]`: a leg elsewhere would be turned away on every run for want of it.
  `init`'s `msvc` toolchain names `visualStudio`. The survey asks every host a leg might land on
  whether it can set it up - Visual Studio's installer, `vswhere`, naming the newest instance with
  `requiresComponent` - and a host without one turns the leg away as `skipped-tool-missing`, naming
  why, one that could not look as `skipped-unavailable`, and one never asked as `poisoned`, a
  defect in this tool; a copy starts nothing and asks nothing. The host that runs the leg sets it up from the
  instance its own survey found, never a second look: that instance's `vcvarsall.bat` for the leg's
  processor (`amd64`, or `amd64_arm64` to cross-compile), run once per environment, instance,
  architecture and host environment by `cmd.exe` from a batch file the harness writes, reading
  `set` before and after it in UTF-16 and keeping only what changed; the batch file names its
  variables `%%NAME%%`, so only `call`'s own pass expands them, and a path holding `%`, `^` or `&`
  is used as it is. Its exit code, an `[ERROR` line in either encoding it prints, a
  `VSCMD_ARG_TGT_ARCH` naming another processor, an instance removed since the survey and a capture
  that cannot be written each fail the leg before anything of it starts: the survey found the
  instance, and an environment that will not set up once a leg began is that leg failing, as a
  program that will not start then is. A capture directory that cannot be removed afterwards is a
  warning and fails nothing. What it set sits over the host's `env` and beneath the variant's, the
  test invocation's and the runner's own, for every process the leg starts, so `cl` builds from a
  plain shell. The programs the leg starts - which no survey can require, since that `PATH` exists
  only once set up - are looked for on it then, before anything of the leg starts: one missing
  there skips the leg as `skipped-tool-missing`, named, never a program failing halfway through a
  build. Each leg's line names it - `developer environment: visualStudio (Visual Studio
  18.0.11205.157, MSVC 14.50.35717, amd64)` - and `--json` carries it as `developerEnvironment`,
  beside the detail rather than inside it, like the compilers.
- **A leg naming a toolchain that does not exist on its own operating system is refused**, by the
  toolchain's `platforms` list. Refused when read rather than skipped when placed, because nothing
  about it needs measuring: a leg's `os` is required, and a leg only ever runs on a host whose
  operating system equals it — emulation varies the processor, never the system. Skipping instead
  would also make the run report a leg that reached no verdict, which is not a success, so a wrong
  list would turn a green run non-zero rather than telling its author which line to fix. A
  project's `defaultToolchain` is held to the same rule, against the platform its key names, or
  against the operating systems its legs declare where the key is `all`.

It is written with LF line endings and no byte order mark on every platform. The file
is tracked, and its bytes must not depend on which machine ran `init`.

## Worktrees

`create-worktree` adds a worktree under the main checkout's `worktrees.root`, which defaults to
`.harness-config/worktrees`, and `delete-worktree` removes one, everything under it, and git's
record of it.

The root is configurable because it is spent before a worktree's own name. The default costs 25
characters of the Windows path budget, and a repository whose build paths are long has no name left
that fits; a shorter root such as `.worktrees` buys those characters back. The budget is still
checked against the real path, so a shorter root never hides an overrun — it only makes one
avoidable. Every build measures the deepest path it left below its build directory against the
reserve, and warns where it went deeper. A Ninja build leaves out of that the outputs ninja says no
target of the current build produces any more - asked as a dry run of `ninja -t cleandead`, which
names them and removes nothing - since a new worktree's build directory, starting from clean, never
holds them; it notes them instead, where one is deeper than the reserve, naming the command that
removes them. A consumer's incremental builds warned every time about the object of a test renamed
away. `ninja -t query` would not have told it apart - ninja's dependency log still knows the path -
and what CMake's configure writes is no output of the manifest at all. A leftover ninja does not
name is still measured, as a target this build had no reason to rebuild, or a file something other
than a target wrote; another generator, or a ninja too old to know the tool, is measured as before. A root spelled with a `.` segment or a doubled separator inside it -
`.harness-config/./worktrees` - is refused with the one spelling to write, and so is a
`sync.exclude` or `sync.neverTransfer` entry spelled that way: each is compared as written, by
sync's lists and by `init`'s ignore rule, so the file system's reading of it would put the
worktrees where nothing withholds or ignores them, and an entry would protect nothing.

**The root is kept in git by a placeholder, and everything made in it is ignored.** `init` writes
`/<root>/*` and `!/<root>/.gitkeep` for it and creates the placeholder, as it does for the
orchestrators directory, `.orchestrators`, for the slots every tree keeps its own copies in,
`.mutations` for a sweep's workers and `.legs` for the copies its legs work in, and for the harness
directories a person fills by hand
— the credential slots `sshItems`, `wslDistros`, `runner/.env`, `runner/.secrets` and
`workflows/.env`, the agent clients' keys, read by the runner's one rule for a directory of dotenv
files and from nowhere else — so a clone arrives with each directory in place. `init` writes the
credential slots' rules and placeholders from the one list of them (`HarnessLayout.SecretSlots`),
which is also the floor that names them for every move of work: seed, fold, refresh and rebase never
touch them, whatever git or the ignore block says (`TreeFloor.OfAgentMoves`). No workflow may be named `.env`. A configuration `init` writes names `.worktrees` as the root; one that names
none keeps `.harness-config/worktrees`, so worktrees made before stay where they are. The root
was ignored whole, with no placeholder, and the costs of this shape were measured then and are
accepted now: a directory holding a tracked file never reads as ignored itself, so `git
check-ignore <root>` answers *not ignored* while every worktree inside it is ignored, and the root
can no longer be kept on another disk through a link, which git would list as an untracked entry
and keep nothing behind; `init` names such a link and writes nothing through it. Sync does not
depend on either answer: it withholds the configured root, `.orchestrators`, `.mutations` and
`.legs` by name, whatever git says, and never deletes them in a copy, and its guard against a
withheld path that stopped being ignored takes each placeholder, untracked until it is committed,
as the harness's own - any other untracked file there still refuses, named as the worktrees root,
or the slot whose ignore rule init writes, it is. The root may not be `.git`, `.orchestrators`,
`.mutations` or `.legs`, or inside any of them. Each copy kept in `.mutations` or `.legs` is a whole
copy of the tree, so a repository's own program that walks the tree from its root must leave out
what the managed block ignores, as git does - `help layout` says so - or it reads every file again
for each copy kept there.

`init` leaves hand-written `.gitignore` rules alone, so a repository that already ignored one of
these paths by hand keeps its rule beside the managed one. A block an earlier build wrote under the
markers of the tool's former name (`GitIgnoreManager.RetiredBeginMarker` / `RetiredEndMarker`, read
for ever and never written) is the managed block too: markers pair by position, whatever their
spelling, so `init` rewrites it in place under the current ones and says so, and the check of
hand-written rules never reads its lines as the repository's own. The machine-wide locks are named
for the tool too (`Global\dss-harness-...`), so a process of a build from before the rename and one
of this build do not exclude each other: update while no `dssharness` runs on the machine. What `init` reports is git's own answer,
never a reading of how the rules are spelled. Each managed rule carries a path it decides - the
file it names, or a name inside the directory it rules on - and `git check-ignore -v --no-index`
says which rule decides each of those paths in the tree. A rule deciding one against the block is
named as the rule git follows: it undoes the block there, whether it is a re-include after the
block, a nested `.gitignore`, or a whole-directory rule such as `.env` - which takes the
`runner/.env` and `workflows/.env` directories, from which git re-includes no placeholder. The same paths are then asked of
the tree's own `.gitignore` with the block blanked out, in a scratch repository holding nothing
else; a hand-written rule that would decide one the other way, where the tree's answer is the
block's, is named as doing nothing there - but only where nothing the harness keeps in git rests
on it. Taken out of the tree's ignore files - its `.gitignore` files, its `.git/info/exclude` and
the excludes file its configuration names - in scratch repositories asked with and without it, such
a rule must turn from kept to ignored no path the block rules on, none of the harness's own files -
its configuration, each placeholder, each file git keeps in an action and a name standing for any
action's, the anchor registries git tracks - and no directory one of those is in; where it would, it
is not named. An action's own files are listed as git keeps them, because a rule can rest on how one
is named: a re-include of the actions directory after the `[Bb]in/` Visual Studio's template
ignores keeps an action's helper in its `bin`, which no name made up for an action's file shows. An allowlist that excludes
`/.harness-config/*`, or everything, re-includes each slot the block keeps a placeholder in - by the
slot's name, or by `!*/` - and git never looks inside an excluded directory for the placeholder:
that re-include is overruled for the slot's contents and needed for the placeholder, and the note
once told a consumer to delete it, which loses the placeholder the block itself re-includes. A rule
keeping an action's files, or the configuration, counts the same way, whatever the block rules on
beside them, and so does one needed only because `.git/info/exclude` excludes what it re-includes.
Only what taking a rule out removes from git counts: a file `*` hid, which taking it out would put
in git, rests on nothing. A re-include of a directory nothing excludes changes nothing, and is
named. No path the check asks about is made a directory in a scratch repository unless git takes
it for one in the tree too - a directory above a probe - so a rule ending in `/` matches there only
what it matches in the tree. A rule agreeing with the block is not named at all: it
changes nothing, and a broad rule covering a managed path is not a copy of the block's rule. The
spelling comparison this replaced named rules that match nothing as overriding the block, and
could not see a rule reaching a managed path through a wildcard.

git's own answer has two blind spots, both measured. It never names a re-include that matched a
directory above the path - the path is then decided by no rule at all - and it matches a rule
ending in `/` against a path only where that path is a directory that exists. So a rule re-including
each host's directory, `!/.harness-config/sshItems/*/`, left a file there to the block while
putting every `.key` in reach of `git add`, and init said nothing; and `!/.harness-config/runs/`
was reported as no rule at all, blamed on another `.gitignore`. Each slot is therefore asked about a
file inside one of its directories too, and where the tree's answer is no rule for a path the block
ignores, the question goes to a scratch repository holding the tree's `.gitignore`, and each
`.gitignore` of its own along the path, with every directory above the path made: there git names
the rule re-including the nearest of them, at the line the tree's file has it on. Those files outrank
`.git/info/exclude` and every excludes file a configuration names, so a re-include undoing the block
is in one of them; were none named even there, init says git does not ignore the path and names no
rule, never where one might be. A path git will not answer about - one beyond a symbolic link, which
git never looks past, ends a whole `check-ignore` with exit 128 - is named with git's reason, and
every other path is still answered: git is asked about the tree's root, which it answers about
wherever it can answer at all - only where it cannot is the question refused, at once - and the
question is halved until each part is answered or is that one path, a few processes for each such
path however many are asked. Each path is asked after `./`, so a name beginning with a colon is never
read as pathspec magic, which `check-ignore` refuses even under `--literal-pathspecs`. A scratch
repository that cannot be written is a note that git could not be asked; one that cannot be removed
afterwards is a warning, and the answer stands.

`init` writes the tree it runs in, a worktree's own included: its configuration, its `.gitignore`
and the placeholders that keep each directory in git. A worktree adopting the harness adopts it on
its own branch; written into the main checkout, the worktree's `.gitignore` never changed and main's
did. A worktree with no configuration of its own gets a copy of the main checkout's, which it was running
with and warned about on every command; a default in its place would drop every leg, host and
runner main declares. What git ignores - connection data, runner values and secrets, the lock - is
read from the main checkout whichever tree asks, and `init` in a worktree says so rather than
creating any of it there.

The main checkout is the one git's `worktree list` names first, which it names for the git
directory its worktrees share: that directory's parent, where it is called `.git`, and the directory
itself otherwise - no checkout at all. A submodule's is named so, kept under its superproject's
`.git/modules`, and `init` in a submodule once took it for a worktree of that directory, where its
connection data, runs and worktrees would have been looked for. Where git names a git directory, the
main checkout is the one that directory's configuration records, as a submodule's `core.worktree`
does; one made with `--separate-git-dir` records none, and is then the tree asked from, where git
lists that as no linked worktree. From a linked worktree of such a checkout, nothing says which
checkout is the main one, and the command is refused, saying how to record it.

`create-worktree` records the commit a worktree was made from, under
`refs/harness/worktree-base/<name>`, and `list-worktree` reports it. A worktree's own HEAD moves
with every commit made in it, so after the first one nothing else says what tree the worktree
started from, and it can only be reproduced from the moment it happened to be made. The record is
kept under `refs/harness/` rather than among heads, tags or remotes precisely so it can never be
mistaken for somewhere work is kept: the deletion checks below read branches, tags,
remote-tracking refs, the newest stash and other worktrees' HEADs, and this record is none of them.
It is removed when the worktree is, so it can never answer for a later worktree of the same name.

Every git command the harness runs, and the forge's command line, which runs git itself, starts
without any variable git reads a repository from: every name `git rev-parse --local-env-vars`
prints, asked of git once per command rather than written down here. They include `GIT_DIR`,
`GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_COMMON_DIR`, `GIT_OBJECT_DIRECTORY` and the two that hold
`-c` settings, `GIT_CONFIG_PARAMETERS` and `GIT_CONFIG_COUNT`. Each silently outranks
`-C <directory>`, and a git hook runs with some of them set, so a harness command invoked from a
hook — or from a shell someone left in another checkout — would otherwise read and write a
repository nobody named: measured with `GIT_COMMON_DIR` naming another repository, deleting a
worktree was refused and left it registered. Settings passed that way do not reach the harness's
git either. A caller that deliberately wants a different index still gets one: the inherited value
is cleared first and the requested one set after. So does one that has git store a file only to read
it back: it is given an object directory of its own, with the repository's beside it to read from,
so nothing it stores reaches the repository. An answer that does not name `GIT_DIR` is not
taken, and the command fails without running git.

An orchestrator's agents' worktrees sit below the directory named for it,
`<root>/<orchestrator>/<agent>`, and are made by `create-agent` with the agent's records, never by
`create-worktree`, which makes plain worktrees only. Each is named by its address,
`orchestrator/agent`, wherever a person reads or types it: `list-worktree` lists it so,
`delete-worktree` takes it, and the commit it was made from is recorded at
`refs/harness/worktree-base/<orchestrator>/<agent>`. Plain worktrees and orchestrators share the
names under the root, so a plain worktree cannot take an orchestrator's name, and an agent's worktree
is never made inside a plain one, where git would take it for part of it. A directory with no `.git`
of its own that holds worktrees below it - an orchestrator's, holding its agents' - is never deleted
as one, `--force` or not: deleting it would delete each of them. A worktree's submodules and nested
repositories are its own contents, and do not make it such a directory.

### What deleting one refuses

Without `--force`, every check runs before anything is touched. A refusal deletes nothing of the
worktree, names everything it found on one line, each with its remedy, and exits 13:

- **Uncommitted changes.** A modified, staged or untracked file git does not ignore, or a
  changed submodule. Status runs with `--untracked-files=normal` and
  `--ignore-submodules=none`, so configuration can hide neither. Status never compares a file
  marked assume-unchanged or skip-worktree, so those present on disk are compared on a copy of
  the index with the marks cleared, which applies git's own line-ending rules and never touches
  the real index. A skip-worktree file absent from disk, as a sparse checkout leaves it, is not
  a change. `--discard-uncommitted` deletes the changes with the worktree while every other
  check still runs, and says how many it discarded, naming a few; a refusal for another reason
  names none of them. `--force` deletes them too, and skips everything else as well.
- **Commits nothing else names.** A detached worktree's HEAD can be the only name for the
  commits made there. They are counted when no branch, tag, remote-tracking ref, the newest
  stash, or the HEAD of another worktree reaches them, so a worktree on a branch, or whose
  commits were pushed, is not refused. A commit held only by an older stash entry is refused.
- **Submodule work.** A linked worktree keeps its submodules' repositories in its own git
  directory, so their branches, tags and stash are deleted with it, whether the submodule is
  checked out or was deinitialised. Every repository there is checked, nested ones included,
  and so is a checked-out submodule whose `.git` is a directory of its own. One stops the
  deletion when commits on its HEAD or branches are on no remote-tracking ref or tag, or when it
  holds a stash. Tags count as kept, since they usually come from upstream, so commits held
  only by a tag made inside the submodule are deleted with it, without a refusal. A directory
  there that looks like a repository git cannot read, such as one whose `HEAD` a crash emptied,
  stops the deletion with exit 20.
- **A lock**, reported with its reason and `git worktree unlock`.
- **A worktree moved by hand.** git removes, and lists, the directory its record names. When
  that is not this directory, the refusal points to
  `git -C <main checkout> worktree repair <path>`. This worktree's HEAD is told apart from other
  worktrees' by its record rather than its path, so a moved worktree's commits still count.
- **Not a worktree of this repository.** The directory must be the root of a work tree whose
  git directory sits in the `worktrees` directory of the main checkout's git directory. In a
  directory whose `.git` file is gone, git answers for the main checkout around it, whose
  status reports none of the directory's files. The refusal points to
  `git -C <main checkout> worktree repair`, which helps only while git still has the worktree's
  record: when it was moved or its `.git` file was lost. A clone at the path would pass a status
  check and take its whole history with it.

- **Evidence.** The directories `worktrees.evidenceRoots` declares are checked before git is asked
  anything, because they hold files git was never told about. One of them holding anything refuses
  the deletion and names it. A worktree's measurements live in an ignored directory precisely because
  they are not source, and deleting them is silent: git reports nothing missing afterwards.
  `--delete-evidence` proceeds while every other check still runs; `--force` proceeds too, and
  skips everything else as well. A declared root that cannot be read counts as holding something,
  because an unreadable directory is not an empty one. A root resolving outside the worktree is
  ignored rather than refused, since the deletion was never going to touch it.
- **Held, on Windows.** Looked at last, once every check above has passed: a worktree something
  holds part of is refused on its own, since git's removal would stop part way on it.
  [Removal](#removal) says what holds one.
- **A copy kept in it.** The workers kept in the worktree's `.mutations` (see *Mutation
  testing*) go before it, and are asked about before anything of it is removed or looked through
  for what holds it: one a sweep still running holds keeps the worktree, exit 13, and one that
  cannot be removed, or told for the harness's - its marker cannot be read - keeps it as a failure,
  exit 20, as workers that cannot be looked for do; nothing is removed. A sweep that takes one
  between the asking and the removal keeps the worktree as well, and so does one whose removal then
  fails, the others gone by then, which is said. An interruption as they go says which went and
  that nothing else of the worktree was removed, exit 130. `--force` deletes the worktree with every
  worker in it, one held or one that could not be removed among them, saying which went with it. A
  directory there under a worker's name that no sync made goes with the worktree, as everything
  else in it does, and is said. The copies its legs work in, kept in its `.legs`, are asked about
  the same way after the workers, each under the lock a run takes on it: one a run is building in
  or replacing keeps the worktree, exit 13, one that cannot be removed keeps it as a failure, exit
  20, and `--force` deletes them with it.

Without `--force`, when the check cannot be finished, because git cannot answer, a record cannot
be read or its directory found, or, on Windows, a directory in it cannot be looked through for
what holds it, nothing is deleted and the command exits 20, with the reason first. Which worktree a directory is, and whether its record is gone, is decided
from paths git reports. A path the harness spelled is compared with one only after every link
along it is followed, so a linked `.harness-config` or worktrees directory changes nothing.

Ignored files outside a declared evidence root are deleted without a check, including ones no
build makes again, such as `.env`, and so are ignored directories with everything in them, the
history of a repository nested inside one included.

### Removal

- Checked, removal is plain `git worktree remove`. git's own check still catches a file changed
  since ours, a file added unless `status.showUntrackedFiles` is `no`, or a lock; a worktree
  holding submodules is removed with `--force`, which git requires for one, and so is one whose
  uncommitted changes `--discard-uncommitted` discards, which git's check would refuse. That
  skips even this check, though git still keeps a lock, which only a second `--force` overrides.
- Checked, on Windows, every entry of the worktree is first opened as a deletion opens it, and let
  go at once. git's removal stops at the first entry something holds - a directory that is a
  process's current directory, a file open without sharing its deletion, a file a program is
  running from - after it has deleted the worktree's `.git` file and git's record of it, and only
  `--force` finishes it then. So a worktree something holds is refused whole, exit 13, with nothing
  removed, naming what is held and saying what Windows said; where that is the command's own
  current directory, it says to run it from outside the worktree. What a deletion goes through
  holds nothing: a watcher on a directory, as an editor or a language server keeps, and a file open
  sharing its deletion and its writing. Moving the directory aside and back was measured as the
  test and is not one: a watcher on a directory under it refuses the move, and a program running
  from it does not. Opening cannot tell every case apart, and each is taken as said: a file open
  sharing its deletion and not its writing is taken as held, as a program running from it is,
  though a deletion goes through it; a read-only file a program runs from is not found, since it
  refuses writing to anyone; and where Windows keeps a deleted file until every handle to it is
  closed - older versions do, and filesystems other than NTFS - a handle sharing deletion still
  keeps its directory from going, and is not found. Linux and macOS need no look, since neither an
  open file nor a current directory stops a deletion there.
- Checked, on Windows, every directory junction in the worktree is then removed, as the link it
  is, never what it leads to. git for Windows leaves every junction when it removes a worktree,
  and every directory above one, while reporting the worktree removed - its `.git` file and its
  record already deleted - measured with git 2.55.0.windows.5; symbolic links it removes itself,
  so they are left to it. A junction that cannot be removed stops the deletion before git runs,
  exit 20, naming it, what Windows said and the junctions removed before it. A volume mounted on a
  directory has a junction's tag and is never unmounted: it stops the deletion the same way.
  A successful deletion names the junctions it removed.
- When git fails part way all the same, as on a file something opened after that look, or when
  it reports the worktree removed while its directory is still there, its record and some files
  may already be gone: the command deletes nothing more, exits 20 and says what is left, and
  `delete-worktree <name> --force` finishes it, which is safe because every check passed before
  removal began. Run again without `--force`, the command names that same way for a directory
  that holds no `.git` of its own and that git records no worktree at, which is what such a
  removal leaves; `git worktree repair` is named only while git still records a worktree there,
  since it rebuilds a lost `.git` file from that record and has nothing to rebuild from without
  it.
- Forced, it is `git worktree remove --force --force`, which overrides a lock. A directory git
  leaves behind is deleted, each junction in it first as a link - the runtime's own recursive
  delete removes a junction and then reports it refused - and git is then asked again to clear
  its record, which it can once the directory is gone. A file that cannot be deleted is reported with exit 20 and what to do
  next.
- The record is confirmed gone by its administrative directory, found before removal, or, where
  git could not name that directory, by git's list. A record whose directory is already gone,
  as an interrupted delete or a directory removed by hand leaves it, is cleared once its lock,
  unreferenced commits and submodule repositories are checked, so the name can be used again.

An interruption stops the command cleanly only before its first destructive step. After that
the deletion goes on, the command says at once that it is under way, and it waits up to two
minutes for the deletion to finish, as does a host agent running it for another machine. Just
before the two minutes are up, git is stopped and the command says what is left. The deletion
can be left partly done on any platform, and on Linux and macOS the interruption reaches git
itself; running `delete-worktree <name> --force` then finishes it.

## Orchestrators and agents

An orchestrator is a session that runs agents side by side, each in a worktree of its own. Everything it and its
agents keep is in the main checkout under `.orchestrators/<orchestrator>/` - records, logs, plans, scratch, the rows
an agent files, its kept evidence and transcripts - never in a worktree, and init keeps the directory in git by its
placeholder and everything made in it out; sync withholds it from every host by name. A session taking the work
over, on another account or after the last one ended, reads it there. `OrchestratorLayout` spells every path once;
`OrchestrationStore` reads and writes the records strictly - a record that does not read as exactly what it must be
is refused, never guessed at - and `OrchestrationLog` appends one JSON line for each run that reached an orchestrator's
or an agent's record, refusals included: create-orchestrator, create-agent, seed-agent and every `--apply`; a dry run is
not logged. Each line names its outcome by its exit code's name, the one table of them. A log that cannot take a line
never replaces what the command did; the command says so instead. Once a command has written, an interruption waits for
it as long as a deletion expects (`PointOfNoReturn`, the one list the command line reads), and what stops it from there
on is exit 21, naming what is in and how running it again finishes.

**What a dry run answers.** A dry run of refresh-agent, rebase-agent, fold-agent or delete-agent exits with what its
`--apply`, given the same arguments, would exit with at that moment: 0 only where that `--apply` would go through.
`DryRuns.Answer` is the one place that decides it: each verb composes its dry run's outcome - its summary and its
lines, or the refusal or failure it met - and hands it over with how the request stands (`RequestStanding`: sound, a
consent nobody gave, or refused; none where what it weighs could not be read) and with who holds what its `--apply`
takes (`TreeHeld`, as `TreeHolders.Read` reads it), and it answers the code and how the request stands as that code
says it (`DryAnswer`), which `--json` reports. Read downwards, the first case that
applies gives the code: delete-agent run from inside the worktree it removes is refused (13), nothing read; what the
command weighs that cannot be read - for a live agent's deletion, which git worktree it is (`StampOf`, the one read
both make) - fails it (20); a path, a row or an option that is refused - or, for a deletion, a record of the copies
hosts keep that cannot be read, which its `--apply` reads before it writes anything (`AgentService.HostCopiesOf`, the
lines weighed kept) - 13, save a usage error, 10, which its `--apply` answers before it takes anything; cells of the
rows that need a consent nobody gave (`--new`, `--accept-lost`, `--accept-over`), or a path both trees changed whose
merge is clean and that `--merge` does not name - a refresh's and a move of a base's too - 13 - the same summary and lines, the line 'to
write them:' (delete-agent's 'to do it:') among them,
since a plan of rows still shows every such cell (`AnchorBatchMode.Plan`) and the rule is the command's, not the
anchors' service's; a run holding a tree the `--apply` takes, or a lock file that cannot be read, 13 - save where what
the command weighs could not be read, or the request is a usage error, either of which keeps its own code from above
and still names the holder on its lines -
the summary of a sound request ending in `TreeHolders.Blocked`, one sentence that says waiting is all there is to do,
and a refused request or a consent ending in the one clause that says a run also holds what the `--apply` needs; a
closed agent whose directory is no
longer a git worktree, 21, as every `--apply` of it is (`Husk`, the one sentence both say); a sound dry run of a
deletion whose copies kept in the worktree the removal cannot take as they stand - a copy that cannot be told or
removed, or copies that cannot be looked for - 21, its request refused, since every `--apply` leaves the removal there
until that is put right, which no waiting does (`KeptCopyLine.Stops`, the summary ending in `DryRuns.Unremovable`),
over a copy a run or a sweep holds as well, while a directory there that no sync made goes with the worktree and is
only said; one that only a run or a sweep holding a copy of the worktree keeps, 21 (`TreeHolders.RemovalHeld`), since
its `--apply` would go as far as the removal and leave it unfinished; otherwise 0. Neither of those two is reached
where a lock file cannot be read or a run holds a tree the `--apply` takes, which refuse that `--apply` at its hold, so
a dry run never says the `--apply` would go as far as the removal where it would not. Every line a dry run
prints stays: only its code, and with it where its summary is printed, follows the rule. What an `--apply` learns only
by doing its work - a file changed after it was weighed, a write or a copy that fails, transcripts or evidence that
cannot be kept - is not in a dry run, so 0 says that nothing known now stops the `--apply`, never that it will finish.

**Who holds the tree.** Each dry run asks the run lock, last, the question its `--apply` asks first, with the very
requests the apply takes (`AgentService.Holds`, the one spelling of them, shared with the hold the apply takes): the
main tree and the agent's worktree for a fold, a refresh and a live agent's deletion; the worktree alone for a rebase,
and for the deletion of an agent already closed or whose worktree is gone. Every live
holder in the way is printed on a `held now:` line (`RunLock.Holders`, read as `HeldBy` reads: nothing reclaimed,
nothing written), said with where that run's leg stands, from this machine's record of heavy legs (`HeavyLegSlots.Line`,
joined by `TreeHolders`, the one sentence: it waits in line with how many are ahead, or it holds a slot, or a leg on
an ssh host's own record says; a run that asked for no slot is said by the lock's own words alone, `running
'<command>'`, and so is every run of a leg of this machine - a WSL distribution's among them, whose slot this machine's
record holds - where that record cannot be read, which one line then says, while a leg on an ssh host asks that host
for its slot whatever this machine's record holds, and that record is read only where a holder's leg is one of this
machine's, so no line says it cannot be read where none is). A run holding a leg copy
or a host's copy of the worktree is
not named by a fold's, a refresh's or a rebase's dry run, and their `--apply` is not refused for it: a copy is not the
worktree. delete-agent, which removes every copy with the worktree, names a copy a run holds - a host's through the
lock, asked in the one reading of it that asks for the trees (`IHostCopyRemover.RemovalLocks`, the same `RemovalLock`
the removal takes, asked by `TreeHolders.Read`, so a lock that cannot be read is said by the one row for it, a host's
copy recorded or not), a leg copy's or a mutation worker's
through the measure-only question its removal asks (`WorktreeService.CopiesHeldAsync`), each in its family's own
words, and asks them on every state the agent may stand in - live, closed or gone - since the removal that asks them
is the same; its dry run asks that host's `.legs` nothing, as its removal asks nothing of them. Each line the two
families say is one of three (`KeptCopyLine`): a copy a run or a sweep holds, whose holder that line names - a leg
copy's asked of the lock as its removal asks it (`LegCopies.Whole`), in that same one reading, so every holder is named
from it, a worker's named by its sweep's claim - and no `held now:` line names again; what the removal cannot take as it stands; or what is only said. The refusal an `--apply`
gives where a run holds what it needs ends with the same words the `held now:` lines say, and seed-agent's, which has
no dry run and so takes the trees at once, does too. `--json` on the four verbs answers with one document
(`OrchestrationReports.Document`), the whole of standard output: the outcome as data - `exitCode`, `summary`,
`applied`, `lines` - with `request`, which tells the three meanings of 13 apart, and `heldNow`, every holder that
decides the code, with `holds` tree or copy - each a `held now:` line names, and each holding a copy kept in the
worktree, its `said` the line its family says of it - its `processId`, `run` and `since` as the lock's entry records
them or, where a claim of the copy's family rather than the lock names its holder, as that claim records them
(`CopyHolder`), and what only a lock entry says, `command`, `variant` and `standing`, null for such a holder;
`standing` is null too for a leg of this machine where this machine's record of heavy legs cannot be read - and
`heldUnread`, why the lock file could not be read. The document is the answer's facts serialised (`AgentVerbFacts`),
which a step of an orchestrator run will read typed, so beside those it says `needs`, each consent the request lacks
by kind - `fold-new-rows` (`--new`), `fold-lost-rows` (`--accept-lost`), `fold-over-cells` (`--accept-over`),
`fold-two-changes` (`--merge`); `refusals`, each reason it stands refused for, `conflict` where a path both trees
changed has a three-way merge that conflicts and `other` for anything else, so a request refused for conflicting
merges alone says `conflict` and nothing else; and `folded`, what the `--apply` of a fold and of a deletion, which folds back what is left of the agent, did - gone through or stopped part way after writing some of it (21): the paths written, deleted, merged and handed, each handed with its digest, the arm registry where it was merged by arm, the registries its rows changed, the ids of the rows applied and the cells `--accept-over` took; null for every other answer and where it did nothing at all; and `planned`, what the plan of the verb names, its dry run's and its `--apply`'s alike: the paths taken, kept, handed and merged, the arm registry merged by arm, the paths a fold - a deletion's among them - writes and deletes, the registry files its rows change, as git names them, and the rows it applies, and what each consent it lacks and each conflict it meets is about (`mergeable`, `conflicting`, `newRows`, `lostCells`, `overCells`); null where no plan was measured. An `--apply`
refused at its hold answers the same document,
`applied` true, its `request` `sound` and the run it met in `heldNow`, said as a dry run at that moment says it
(`TreeHolders.Refused`, the one sentence and holder both take, handed up by the hold to the verb's answer), so a
script waits on it as on its dry run. A command stopped before
anything is weighed - a command line the parser refuses, a `--directory` that is not there, a repository not
initialised, another usage error, or a refusal met before then, an agent that is not there among them - answers with
the same document, empty (`OrchestrationReports.Stopped`): `applied` false, `request`, `heldNow` and `heldUnread` null, no `lines`, no
`needs`, no `refusals`, and `folded` and `planned` null, its `exitCode` and `summary` the code it exits with and the line it ends on.
Each of these documents, and list-orchestrator's, also says `ended` where finding the orchestrator ended a session a
killed process left running (see containment, above): one object per session or group, its `orchestrator`, `agent`
(null for a step no agent owns), `step`, `session` (the leader's id), `group` (whether it was the process group of
its legs' work), `program`, `owner` (the process id that had gone) and `unreached` (null where everything in it was
reached); with nothing ended there is no `ended`, and the document is what it always was. No
command writes it: the operation keeps what its sweeps ended (`EndedChildSessions`, one per operation), and its session
writes it into whatever document the operation answers with (`IDocumentFacts`) - its own, or the stopped form where it
answered without one, threw or stopped after the sweep - so a command over an orchestrator added later says it too.

An agent's worktree is `<worktrees.root>/<orchestrator>/<agent>`, addressed `orchestrator/agent` by list-worktree
and delete-worktree, its host copies named `orchestrator--agent` (`WorktreeAddress`). The directory named for an
orchestrator under the root is shared with plain worktrees' names, so create-worktree refuses an orchestrator's
name and create-orchestrator a worktree's, and delete-worktree never deletes a directory holding worktrees below
it, forced or not: below the directory named for an orchestrator, every directory with a `.git` of its own
counts, and below any other directory with none of its own only what git records - a husk's submodules are its own
contents, which `--force` deletes with it. A directory with a `.git` of its own below an orchestrator's that git records
no worktree at is what a removal left when git's record went and something in it could not, or a mutation worker an
earlier version of this tool kept beside an agent's worktree, which `clean --former-workers` removes: nothing else here
can tell what it holds, so `list-worktree` says it as a person's to look in and delete, and delete-orchestrator
refuses, exit 13, changing nothing, until it is gone. Where git cannot list its worktrees, nothing is deleted. delete-orchestrator removes
every record last - each agent's after the rest of that agent, the orchestrator's after its agents - so a removal that
stops part way (exit 21) leaves only records that read, and run again it finishes. list-orchestrator counts an
orchestrator's open agents as create-agent does (`OrchestrationRules.OpenAgents`), and says where each agent's
worktree is from its own record, the worktrees root it was made under.

An agent can also be made with no worktree (`create-agent --no-worktree`): a record and the two directories every
agent gets outside a worktree - `work/<agent>/` and `plans/<agent>/` - and nothing in the tree. Its record says
`"access": "read"` (`AgentRecord.Access`); a record that says nothing is an agent with a worktree, which is every
record an older tool wrote and what the tool still writes for one, so those records read as they always did. The
engine makes one for a read-only reason step, and nothing else does - the option's help says a person has no use for
one by hand. It counts against no limit, the limit being a limit on worktrees: `OrchestrationRules.OpenAgents` leaves
it out, and so do list-orchestrator's open agents and delete-orchestrator's way, since a live one holds nothing but
records and goes with the orchestrator; what every agent's directory keeps of the steps that ran as it and of its
sessions with an API client is counted with its evidence and transcripts, and goes only with `--delete-evidence`,
whatever the agent's kind or state. It is never seeded, refreshed, rebased or folded - each refuses, exit 13, once the
repository is found and before git is asked anything about the agent - and deleting it is one write, `live` to `deleted`, the path the states already have
for an agent whose worktree was never made; its directory stays, as every deleted agent's does. It records no session:
`--session` beside `--no-worktree` is a usage error, and such an agent's record that names one does not read
(`AgentRecord.Problem`), so deleting it says no session is recorded and looks for no transcript, and keeps no evidence
either, having no worktree. `--discard-uncommitted` and the allowances have nothing to act on there; delete-agent says
so, line by line, and its dry run's line for `--apply` leaves them out.

A `dssharness` from before the agent with no worktree reads its record strictly, finds no worktree in it, and cannot
tell what the agent is: its list-orchestrator prints the agent as one whose record does not read, and its
delete-orchestrator refuses that orchestrator and changes nothing. As across the tool's rename, update while no
`dssharness` is running, and do not go back. An agent with a worktree is not touched by this - the tool writes no
`access` for one, so its record is what an older tool reads.

### Seeding and folding

The seed is the main tree's uncommitted state handed to the agent when it is made - every path git status lists,
less `TreeFloor`: `.git`, `.orchestrators`, `.mutations`, `.legs` and the worktrees root, asserted where the copying
happens rather than left
to ignore rules a repository can edit away - each changed file copied into its worktree, with the copy's SHA-256 and,
where the platform has one, its execute bit, and each deletion made there too and recorded as absent. `seed.json` is
then what the agent shares with the main tree, path by path. An untracked directory git will not look into - a
repository of its own - is named and not handed, since a fold never moves one, and a path in a submodule the agent's
worktree records - at or below a gitlink its index holds, or for a worktree not made yet the main tree's HEAD, where
the main tree turned one into files of its own - refuses create-agent before the worktree is made, and seed-agent and
refresh-agent before anything is written (`AgentFold.InSubmodulesAsync`): a hand-over never writes into another
repository, and rebase-agent, whose move takes the index along, brings in what the main tree committed there. A
symbolic link is refused rather than
handed over as the file it leads to, before the worktree is made, so the refusal leaves nothing behind. seed-agent
counts as the agent's own only a change of what it shares that it made itself. What the main tree holds later is
weighed against what the agent shares (`AgentFold.MovedAsync`), never against git status alone, which forgets a path
the main tree commits or puts back as its HEAD holds it: a path the agent was handed or folded is stale once the main
tree's copy is not the one its seed records, and any other once the main tree's copy - committed since or not - is not
what the agent's base holds, asked of git as a fold asks it. refresh-agent hands over every such path under the paths
it is given - or anywhere in the tree, off the floor, with `--all` (`RefreshRequest`), which names no prefix and takes
no path beside it - refused, copying nothing, where the agent changed or deleted one of them (`EditedAsync`, the same
comparison asked of the agent's worktree). What it does is measured in one place, beside the fold's and the move's
(`AgentFold.MeasureRefreshAsync`, a `RefreshPlan`): a path the agent already holds as the main tree does - the same file,
or no file in either, a directory of the agent's own files there included - is recorded as shared at what both held
when it was measured, the file whole with its bit, its bytes kept from the agent's copy (`AgentFold.AlikeAsync`),
listed, and written only with `--apply`, so its record says
what it holds and the next move of the main tree hands it; dropped unrecorded, as before, the agent's copy read as its
change from then on. `--stale <path>` hands the main tree's copy over a path the agent changed only where its copy is
proved the main tree's own at an earlier commit (`AgentFold.StaleAtAsync`, asked once for every path a refusal or a
`--stale` names): the agent's copy as `git add` would record it in its worktree (`ListStagedIdsAsync`, read as the
merge's versions are, below), so a copy that differs from a commit's file only by a line-ending conversion is that
commit's file, one committed with carriage returns keeps them as git keeps them, and an edit is its own bytes wherever
git leaves files unexamined (`core.ignoreStat`), against the blobs the main tree's
history held at the path (`ListHeldInHistoryAsync` from its HEAD, `rev-list --full-history` so a version a merge dropped
is still its history's, at most `TwoChanges.MostCommits` commits, each run of equal blobs named by its newest commit).
Bytes that history holds are the main tree's own there, so replacing them loses nothing it does not keep; the bound
limits the cost only, and is said where it is reached. A proved copy is handed and recorded like the rest
(`RefreshPlan.Stale`), listed apart with its commit; one not proved, or a deletion, is refused, nothing copied; one the
main tree did not move is weighed too, where the agent's copy is not the main tree's, and one the main tree holds a link
or a directory git will not look into at is named as itself among what is not handed, never a copy to prove stale. The
refusal over the agent's changes names each changed path - with the commit whose bytes a qualifying copy holds beside it
(`RefreshChange.StaleAt`) - and the arguments that take each out of what is handed: `--stale` for a proved one, and
`--except <path>`, once for each, which leaves a path as the agent changed it, so the rest goes over and it stays the
agent's change, weighed by its fold against what the main tree holds; it is listed in each report of what was handed,
would be, or needed no handing, and one naming a path the refresh would not have refused is refused as a stray
`--settled` is. A path the agent changed that the main tree moved too, each tree holding a file there, is weighed in
full as a fold weighs one (`TwoChanges`, from what the seed records or the agent's base holds, found as git spells the
path; the main tree's file the side merged into) - two such paths that differ in case alone, one file in the agent's
worktree where it finds a name in any case, once, under the name the main tree's git index spells the file by, from the
base's entry found in any case, the others - one left as the agent changed it among them - no file of the main tree's,
whatever its file system finds under them: what a merge gives stands beside the path
in the refusal (`RefreshChange.Merge`, the offer's clause
without the way on a fold names, `TwoChangeOffer.Gives`), the conflicts are shown, and the `--merge` arguments that take
the clean ones are named. A clean merge `--merge` names - or the arm registry's, merged by arm unasked and judged as a
registry of the agent's worktree as the refresh leaves it, with each path it hands read from the main tree
(`TreeAfter.Copied`) - is written into the worktree once everything else is handed, over only what was weighed
(`AgentFold.MergeRefreshAsync`), and the path recorded as shared at the main tree's bytes, kept from its file: the
agent's copy differs from that, so it is its change, which its fold writes, and it is what that copy descends from, so
the next merge starts there. A `--merge` naming no path under the paths refreshed is refused as a misspelling; one
naming a path with nothing left to merge is let stand, so a line that ran once runs again. A refresh that stops part
way says the line that asks for it again with everything it was asked (`RefreshRequest.Arguments`): its paths or
`--all`, and each `--except`, `--stale` and `--merge`. What is left out is still weighed as in the way of
what is handed. seed-agent hands the same paths besides the main tree's uncommitted state. A
symbolic link the main tree committed is named and never handed; one it has not committed refuses the hand-over. What a
commit holds at a path is told apart - a file, a directory, a submodule's entry, or nothing (`IGitClient.HeldAtAsync`,
one `cat-file --batch-check` process, and the commit's listing for what that does not answer as a file or a directory:
git 2.43 answers a submodule's entry naming a commit the repository lacks as missing, as it does a path the commit does
not hold) - and a directory is a repository of its own only where it holds its own `.git`,
or the index holds a submodule's entry there: named, never handed. Any other directory holds no file at its path, its
files weighed on paths of their own, so a file the main tree turned into a directory is handed as the file's deletion
and the directory's files, and a directory it turned into a file as the files' deletions and the file - every deletion
made first, with the directories it empties, then every file copied. What the agent holds of its own where a hand-over
needs room refuses it, forced or not, before anything is written (`AgentFold.InTheWayOfHanding`): a file or a link at
a part of a handed file's path, which a copy would stop at or write through, out of its worktree, and anything in a
directory where a handed file goes that the hand-over does not delete. What the agent's worktree holds at each path
handed is measured with the rest (`AgentFold.HoldingAsync`) - the file whole, its digest and whether it could be run -
and the hand-over writes over or removes only that (`AgentFold.HandAsync`, asked before each path): a file the agent's
session wrote since - or made runnable, where the platform keeps such a bit - stops it there, exit 21, naming
it, with what it handed recorded, for a run again to weigh anew - seed-agent's forced hand-over included; create-agent's,
into a worktree just made, asks nothing. A making whose hand-over stopped, for this or anything else, says so in its seed
(`SeedRecord.HandOverStopped`): the agent is not whole (`AgentService.MakingUnfinished`) - create-agent run again says
so, never that it is already as asked, and fold-agent, refresh-agent and rebase-agent refuse it - until a seed-agent
hands it the rest, which the engine's create-agent step run again does, forced. Neither hands anything while a move of the
agent's base stands stopped part way, or its HEAD is off its base. Both say when the agent's base is not the main
tree's HEAD, seed-agent --empty too. A path named otherwise than in UTF-8, committed or not, refuses any hand-over,
named as git's quoting writes it: no file opens here by such a name.

Every path recorded as shared - handed, folded, or found already in the main tree - is recorded by one recorder
(`SharedLedger`), which keeps the bytes it records as well (`SharedCopies`): a plain file under the agent's
`shared/`, named by the SHA-256 the seed already records for the path, read from the tree's own file, required to
hash to that digest and written anew as a file of the agent's own - never read-only, never one that can be run,
whatever the file it was read from - and proved (`VerifiedFileCopy.WriteAsync`), so no copy is kept that is not the
bytes recorded. A digest whose copy still holds its bytes is kept already, and is not written again. They are what
both trees held at a shared path, kept to merge from. `seed.json` gains no member for them - it names digests, and a digest
names a file - so a seed written before any copy was kept reads as it did, and has none; a copy is read back by
hashing it again, and counts only where it still hashes to its name. A command writes in one order: the trees, the
kept copies, the seed, and then the prune of the copies to what that seed names (`AgentService.WriteSeed`, the one
place a seed is written). Stopped anywhere, the seed never names a path the trees do not hold as recorded, and a copy
the seed does not name goes at the next write. A file above 16 MiB (`TwoChanges.MostBytes`) is recorded and not
kept. A copy that cannot be kept or pruned never fails the command that met it: the path is recorded all the same,
and one line says so however many paths it was - which, as many as a line names, how many more, and why - since a
full disk would otherwise say it for every file a command records. Deleting an agent keeps the copies of what its fold
writes as any fold does, and what a history search finds: a deletion that stops after its fold leaves the agent live,
its seed naming bytes only those copies hold, and a later fold of a path both trees then change merges from them. What
is said of a copy not kept is said only where the agent outlives the command; once it is closed they go with it.
Every command that writes an agent's records names where they are on each way out once it has -
`record`, `seed`, `shared <directory>` where the agent keeps any, `applied rows` - each that is there
(`AgentService.Records`). list-orchestrator `--json` names the directory as each agent's `shared`.

Merging two changes of one file is git's work, never a comparison of lines made here (`GitClient.Merge.cs`). Each of
the three versions - what both held, and each side's - is first read as git would store it at the path
(`IGitClient.ReadStagedAsync`), as `git add` would record it there: a blob as it is, and a file or bytes in hand as
the file at the path would be (`git hash-object -w --path=<path>` run at the work tree's root, then `cat-file
--batch`). The path's clean filters run from the root, as git add runs them, and its attributes and line-ending rules
apply: a file one tree checked out with other line endings merges as `git merge` would merge it, and one committed
with carriage returns, under `core.autocrlf` or a `text=auto` attribute, keeps them on every side, as git keeps them,
so a merge git makes clean is clean. git stores a file to read it so, and what it stores goes to an object directory
of the question's own - a temporary directory as `GIT_OBJECT_DIRECTORY`, the repository's own as
`GIT_ALTERNATE_OBJECT_DIRECTORIES`, quoted so a path holding the character that parts that list is still one entry -
discarded with the answer: neither the repository's objects nor its index are written by a merge that was only
measured. What git would record for a file is asked in one place (`GitClient.StoredIdsAsync`), of `hash-object` at the
work tree's root, told the two things `git add` reads in the index and hash-object does not (`GitClient.StoringAsync`).
Where `git ls-files --eol` says the path's entry holds carriage returns (`i/crlf`, `i/mixed`) and its line endings are
converted only as git sees fit (no attribute under `core.autocrlf`, or `text=auto`), git leaves that conversion out,
and hash-object is told to as well: `core.autocrlf` off; the bytes as they stand, where nothing else converts the
file; or, where a clean filter, `ident` or `working-tree-encoding` still does, the path's attributes declared whole
with `text` unset, in a tree of the question's own git reads attributes from instead (`--attr-source`, git 2.41's). A
path below an attribute file the work tree lacks, which git reads from the index, has its attributes declared so too.
An older git, which refuses that option as unknown and has no other way to be told, is refused before it is asked
anything for the file, naming git 2.41 and the version installed, which is asked of git once
(`GitClient.InstalledVersionAsync`). info/attributes outranks that tree: where it says otherwise of the path, the question is refused, said, never guessed.
Nothing is staged, so no index is written and no mark on an entry stands for a file's bytes; every index the tool does
write of its own is written with `core.ignoreStat` off, in one place (`GitClient.RunCoreAsync`), so no comparison made
with it reads an edited file as its entry's blob. `GitClient.HashAsync` is the one caller of `hash-object`, told what
differs - written or not, filtered or not, as which path, what git is told first, into which directory.
The three are merged by `git merge-file -p --diff3` (`MergeTextAsync`), outside any repository. Its exit code is the
answer and is read as one, never as success or failure: 0 is a clean merge, whose output is the merged bytes; 1 to
127 is that many conflicts, 127 standing for as many or more; anything else is no merge - a binary file, for one -
kept in git's own words. `--diff3` is passed so no configuration changes what a conflict shows, and the markers are
made one longer than the longest line of the three that could be read as one, so a file that holds such lines is
merged like any other. A conflict is shown from its opening marker to its closing one, six of them and forty lines of
each at most. How many hunks each side brought is git's count too (`git diff --no-index -U0` of each side against
what both held). The merged bytes are brought to what a checkout would write (`ReadCheckedOutAsync`: `cat-file
--filters --path=<path>`) and written as a copy between trees is - beside the destination, read back, compared by
SHA-256, put in place in one step (`VerifiedFileCopy.WriteAsync`) - with the execute bit asked for
(`IFilePermissions.SetExecutable`, which changes nothing on Windows, where no such bit is kept), not the bit of the
file replaced. What a history held at a path is asked with `git rev-list --full-history` (`ListHeldInHistoryAsync`),
since a version a merge dropped was still that history's, for one more commit than its bound, which is how a history
cut short is told from one read whole, its paths taken literally (`--literal-pathspecs`), so `[id].tsx` is that file
and never a pattern; and what git's attributes declare for a path is asked of `git check-attr`
(`ReadAttributesAsync`), by this and by fix-line-endings alike, whose refusal where git cannot answer reads as it
always has: `git check-attr failed, so the declared policy could not be read and nothing was rewritten: <git's
words>`. Every answer read as bytes - a blob, a merge, a checkout, a listing of names - is decoded from its first byte
under the encoding asked for (`ProcessRunner`): bytes that begin as a byte order mark does are the file's, or the
name's, never taken to say how the rest is written.

rebase-agent moves an agent's base to the main tree's HEAD (`AgentFold.MeasureRebaseAsync`). Each path the two commits
hold differently (`git diff --name-only`, commit to commit) is shared - kept as its seed records it, since the seed,
not the base, is what a shared path is weighed against - or held as the new base holds it already, or held as the old
base holds it, and comes in as git holds it (`git checkout --no-overlay <commit>`, the paths on standard input and read
literally), or changed by the agent - asked of git against both commits, with anything where the old base holds
nothing its own, untracked, ignored or staged - which refuses the move unless `--settled` names it. A file the agent
changed that the main tree committed a change to is two changes of one file, resolved where a fold resolves one
(`TwoChanges`): what both held is the old base's blob, ours the new base's, theirs the agent's file, merged in the
agent's worktree's forms, and the bit from the two bases' entries and the agent's file (`ListEntriesAtAsync`, which says
too where a base holds a link). A link among the versions - a base's entry, or the agent's file where it is one or is
reached through one along its path (`LinkPaths.LeadsElsewhere`), its directory turned into a link - is offered no merge,
decided where a fold's is (`TwoChanges.OfferAsync`, from `TwoChangeSide.Link` and `TwoChangeAncestor.Committed.Link`),
so nothing is read or written through it. Its refusal says what the merge gives and names the command
that takes the clean ones; `--merge` takes one, listed before what comes in, written over the agent's file as weighed
(`AgentFold.WriteMergeAsync`, the one writer of a merge) once git has written the rest and before HEAD moves, and
recorded nowhere: on the new base it is the agent's own change, which its fold weighs. A `--merge` naming a path the two
commits hold alike is refused as a misspelling, and one naming any other path they changed is let stand, so the command
that finishes a stopped move runs again; a path named with both `--merge` and `--settled` is a usage error, exit 10. The arm registry is merged by arm as a fold merges it (below): what both
held is the old base's blob as git stores it, or nothing where the agent made the registry the new base brings; ours is
the new base's blob as a checkout into the agent's worktree writes it (`CommittedBytes.CheckedOutAsync`, the one reader
of a commit's blob in that form, which `TreeAfter.Moved` reads through too), so the merge ends its
lines as that checkout would; and the merged text is judged by the worktree as the move leaves it, each path the move
takes read from its blob when the validation asks for it (`TreeAfter.Moved`). One thing the agent
made is not its change: a file that is the very file the new base holds there, which is held as the new base holds it
already (`IGitClient.ListHeldAsAtAsync`: the new base's entry for each such file put in an index of the question's own,
and `git diff` asked against it, so the content is compared through the clean filters and the line-ending rules and the
mode where the repository trusts one, as status will compare them once the index has moved - whatever the worktree's
own index says of the path, nothing for an untracked file). What each commit holds at a path is told apart (`HeldAtAsync`): a directory the old base
held, now a file or a submodule's entry, is held as the old base holds it while the agent's worktree holds a directory
or nothing there, and a submodule's entry as git compares it, so neither is taken for something the agent made.
Nothing of the agent's is written over or hidden: git removes a file or a link where a path it writes needs a
directory, removes a directory, with all it holds, where it writes a file, and hides all a directory holds where it
writes a submodule's entry the old base did not hold - a submodule's own checkout it leaves alone - so each refuses the
move unless it is the old base's own, coming in with the rest - the agent's own, settled or shared, each named with
what to do. A file or a submodule's entry where the old base held a directory is written last
(`RebasePlan.WrittenLast`), once what was below it is: written first, git removes that, with the directories it leaves
empty, where asked for both at once it finds that gone and stops, and a submodule's entry written first leaves it in
place, hidden from git. Where the worktree's file system finds a name in any case, a file the new base renamed in case
alone is that one file (`AgentFold.RenamedInCase`): what the worktree holds at the new name is the file at the old one,
never a file the agent made there, and the new name is written last, once the old is removed - written first, the
removal of the old name would take it. It comes in only with its old name: where the agent keeps what it holds there -
declared settled, shared with the main tree, or its own change - the new name refuses the move, naming both, since
writing it would write over that; a shared file the agent changed is to be folded first, since no refresh hands a path
the agent changed, and one it did not to be refreshed under the new name, which records the file it holds as shared
there. Where it holds nothing at the old name, the new name refuses the move as a rename and a deletion of one file -
its old name to be put back with `git checkout HEAD` and the move run again, the new name deleted after only where the
agent's session deleted the old, or, shared, never taken by a move and so the agent's deletion, folded and committed in
the main tree - unless the move being finished removed it, the new name written last, as its record says
(`MovingRemoved`: the old names the agent's index no longer holds once the git checking out what the move writes first
ends, having written it all or stopped part way). git leaves the index and the disk alike after that checkout and after
a `git rm` of the agent's session; a removal of the move's its record missed - a move killed between git and its
record - is refused so, costing a refusal, never a file. The record names where the move goes (`Moving`) before
anything is written; then each
path the move takes is asked to hold what was measured there (`RebasePlan.Held`, `AgentFold.RequireTakenAsync`), so a
file the agent's session wrote since stops the move, exit 21, before git writes anything, never written over; then the new
base's paths are written, HEAD and the index move (`git reset --mixed`), then the record and the worktree's base ref
name the new base: a move that stopped part way, exit 21, is finished where it was going by running rebase-agent
again - the command its last words name, with every `--merge` and `--settled` the move was given, a merge it wrote
merged again to the same bytes - whatever the main tree committed since, and with what it wrote held as the new base holds it, a file the old
base lacked included - and fold-agent, delete-agent, seed-agent and refresh-agent refuse the agent until then, naming
that same command, which the record keeps beside where the move goes (`MovingWith`). Only a move its record names is ever finished, and only from its base or where it goes: a HEAD anywhere else was
moved by hand - back, forward or beside its base, a move under way or not - or names no commit, and each command
refuses it, saying how to put it back; one past its base that the main tree's history does not hold is a commit made
inside the agent. Where git cannot say which a HEAD is, each fails, exit 20, saying so, never guessing.

An agent's contribution is a measurement (`AgentFold`): its worktree's status, and every path it shares with the
main tree whether its status lists it or not, less the shared paths left as they were. Each path goes in exactly one
list - its own, deleted, inherited, already in the main tree, merged, settled - or is refused, and one refusal refuses the whole
fold before anything is written. A shared path is compared with what both trees held - a file, or its absence; any
other with the blob at the agent's own base (`cat-file --batch-check`, one process), never the main tree's HEAD, which a
sibling's committed fold moves, and the main tree is asked whether it moved from that base as git status would answer
(`git diff --name-only` against the base, one process): through the index's line-ending rules, so a file only checked
out with other line endings is no change, and with its mode, so a sibling's changed execute bit is one. A fold records
what it wrote, removed and found already in as shared - each at what the agent's worktree held when it was measured,
the file whole with its bit, so a bit its session changes after the weighing is the agent's own change to fold, never
silently absorbed - so a later fold - after a review sends the agent back - weighs
those paths against what the fold left, never against the base: the agent putting a path back as it was is its change
to fold. A refusal of a main-tree path the agent shares nothing for, made, changed, made a directory of or removed since
the agent's base, says whether a commit, an uncommitted edit or a commit and then an edit did it, since each is
reconciled differently: a commit alone where the main tree's HEAD already holds what the main tree holds there now,
which `git status` - untracked files listed, its paths compared in any case where the main tree's file system finds a
name in any case, so a rename of case alone is the commit's - answers by listing nothing at the path, below it, or in a
directory above it that it folds into one
entry, as it folds an untracked repository nested in the tree (one process a fold, asked only where a commit moved a
path or neither HEAD nor the base holds one); an uncommitted edit alone where HEAD holds what the base holds, each entry
compared as git compares a commit, so a commit of a mode alone is a commit; and both otherwise, each said with what it
did - the edit after what the commit did where the edit made what the main tree holds of another kind than HEAD's, and
the commit then the edit's change where the commit already made it of that kind. A file of the main tree in the way of
the agent's - where one of its files needs a directory, or in a directory one replaces - is a file the fold does not
remove, said made or changed since the base where the main tree did either, the same way, and a file git ignores
where neither HEAD nor the base holds one - as `git check-ignore` says, asked only of what status lists nothing at
where HEAD holds nothing and no submodule holds the path, never read from what status leaves out - is said ignored by
git, with no word of when it came. A file of a submodule of the main tree's in the way - below a gitlink its index
holds, where git looks at nothing and `check-ignore` answers nothing - came as that record did: by a commit where HEAD
holds it, then changed by an uncommitted edit where status lists the record, and by an uncommitted edit where only the
index holds it (`Came`, the one wording of how a path came to be held, and `Since`, of what the main tree did, both
decided once). The base's entry and HEAD's are found alike, as git spells the path and, where the main tree's file
system finds a name in any case, in any case, whatever `core.ignorecase` says, so a rename of case alone is the
commit's that made it, and none other's. Each tree's status is read alike: where `core.ignorecase` says the
repository heeds case, an untracked name the index holds under another spelling is dropped only where every
directory the two spell differently lists the untracked spelling alone, finds it in another case it does not list,
and reaches something by the index's spelling, and the part they spell differently is ASCII - decided file by file,
as each directory compares names, since a tree may hold a directory that tells names apart below one that does not
(`GitClient.IndexedInAnotherCaseAsync`, read by the one status question and by `check-root-litter`). So a file the
disk spells otherwise than the index - as a write put in place over it leaves it on macOS, which keeps the old
spelling where Windows takes the new - is one file, never changed under one name and untracked under the other,
while a new file beside a tracked one a directory tells apart, or beside a tracked one deleted, stays listed. Left as
it is: where the part spelled otherwise holds a character outside ASCII, the name is kept, on every system - a disk
may hold it composed otherwise in Unicode than git gives it, and the invariant culture this build reads names in
composes nothing - so with `core.ignorecase` false such a file is listed under both spellings, a fold of it stops
part way at the second, and `check-root-litter` names it. git is never told to ignore case: that would match the
repository's ignore rules in any case too, and hide a new file they do not ignore as it spells them. Whether a
directory finds a name in any case is asked of the directory itself, by a name it holds in another case
(`PathCase.In`): one made case-sensitive under folders that are not compares exactly. A shared path's
refusal says the main tree changed it after the agent was last handed it or folded, which the seed records alike. A deletion needs the same baseline proof a copy does. A directory with no `.git` of its own
holds no file at its path, so a file the agent turned into a directory folds as the file's deletion and the directory's
files, and a directory it turned into a file as the files' deletions and the file: every removal is made first, with
the directories it empties, then every file written. A path reached through a link in either tree, a repository of
its own - a submodule, or the agent's own - a path in a submodule of the main tree's - at or below a gitlink its index
holds, said made by a commit or staged by an uncommitted edit, and left out where settled by hand, since nothing a fold
wrote there could be committed in the main tree - what the main tree holds in the way of the agent's files (a file
where one needs a directory, or files the fold does not remove in a directory one replaces), a HEAD off the base and a move of
the base stopped part way are refused, and so is an anchor registry the agent changed as a file: its rows go in through its rows directory, weighed
against the registry the fold would otherwise have written over. `--settled` is the way out of any path's refusal -
`--merge` only of a path whose merge is clean - and a deletion can be settled too; a settled path the fold does not
weigh is refused as a misspelling. So is a `--merge`
naming no path the fold weighs, while one naming a path it weighs with nothing left to merge is let stand, so the
command line of a fold that stopped part way runs again. A path named with both `--merge` and `--settled` is a usage
error, exit 10, before anything is read, and a `--merge` naming an anchor registry or the arm registry is refused. A path the main
tree already holds as the agent does is already in, so a fold run again finds its own writes. Where the agent's
worktree finds a name in any case, two paths it changes that differ in case alone are one file there; where its git
index spells that file otherwise than its base does, found in any case, the agent renamed it in case alone, and
each path is read only where a tree spells the file so (`AgentFold.Spellings`) - as its git index does, where it holds
the file under one spelling, since a rename git makes in the index alone leaves a directory's name on disk as it was,
and otherwise as its file system does (`PathCase.Spelled`) - never as the other. Where the main tree's git index spells
that file otherwise, each tree holding it, and the main tree finds a name in any case too, a fold cannot
write the rename - the main tree's git would go on naming the file as before - so it is refused by name, saying to run
`git mv -f` there, forced since git finds the new name taken where the repository does not say it ignores case; once
that is run, the old name is already in, absent from both. Every path the agent shares nothing for is weighed against
the base's entry found as git spells it, as `Came` finds it, so a file renamed in case alone is weighed against the
base's under its old name: an edit made with the rename merges with the main tree's, and a removal of the file in the
main tree refuses it, never undone. A path this process
cannot look at is never read as absent, which would take the agent's file for its deletion: the fold fails before
anything is written. A file of either tree that changed after it was weighed is never written over: the agent's copy is
refused where it no longer holds the SHA-256 weighed (`VerifiedFileCopy`), and the main tree's file is weighed again
before each write and each removal. The fold stops there, exit 21, for a run again to weigh it anew.

A path named with `--settled` was reconciled in the main tree by hand, and the fold acts on it (`AgentFold.MeasureAsync`,
in this order): one leading out of the main tree is refused, as any is; one reached through a link in either tree, or a
directory git will not look into in either, is left out and said to be (`FoldPlan.LeftOut`), since a fold never reads
or writes it; one both trees and the seed agree on is inherited, so the line that ran once runs again; one the two
trees hold alike is already in, and recorded; one where the main tree holds other than what the agent last shared - or,
for a path it shares nothing for, than its base - is handed (`FoldPlan.Handed`); and one only the agent changed is
refused, since nothing was reconciled there and handing the main tree's copy would undo its work. What is handed is
sorted as any hand-over sorts what the main tree holds, asked what of the agent's own it would go over or through
(`InTheWayOfHanding`), and measured in the agent's worktree; `ApplyAsync` hands it last, after every write of the main
tree, through the one loop every hand-over takes - deletions, then files, each asked first to be what was weighed - and
`FoldedAsync` records it through the ledger. Both trees and the seed then agree, so the agent's next fold starts from
the reconciliation, and delete-agent's proof that nothing is left to fold holds without being told which paths were
settled. rebase-agent's `--settled` is another option: there the person reconciled the agent's copy, which stays as its
change on the new base.

A path both trees changed - each holds a file there, neither is what the two last agreed on, and they differ - is
resolved in one place, whichever command meets it (`TwoChanges`, made for each command with that command's
`TwoChangePolicy`: the arm registry's path, the anchor registries' and the paths named with `--merge`). Every such
path is weighed in full before anything is written, asked for or not, and what a three-way merge of it gives ends
the path's refusal: clean, with the hunks each side brought; conflicting, with the conflicts shown under the
refusals; or none offered, with why - one of a closed list (`NoMerge`) that owns each reason's sentence, so no
command words one itself. The refusal then names the command that takes the clean ones. Asking, `--merge <path>`
once for each, changes one thing: a clean merge is taken. None is offered for a deletion against a change, a path
both trees made with nothing either held there before, a version that is a link or is reached through one, a file
above 16 MiB (`TwoChanges.MostBytes`, the bound a kept copy has too, asked by one owner for every rule - of the files on
disk before anything is read, and of every version once read, since a blob's size is known only then:
`TwoChanges.WithinBoundAsync`), one whose `merge` attribute asks for another merge than git's own of text, or one git
reads as binary or will not merge; and an anchor registry, which changes only through the rows an agent files, and the arm
registry are never merged by line. The arm registry - the configured `mutations.registry` - is merged by arm instead,
with no option given (`TwoChangeRule.Arms`): its three versions go to `MutationRegistryMerge.Merge` as the bytes each
tree holds, nothing staged and nothing git merges, so the merge ends its lines as the main tree's file does; what both
held is found as for any path, and is nothing only where neither tree held a registry before - shared bytes that cannot
be found offer no merge, since a merge from nothing takes an arm one side removed for the other's addition. A clean
merge is taken only once its text (`Merged.Text`) reads as a registry of the tree the command leaves
(`TwoChanges.JudgedAsync`, through `MutationRegistryValidation.ValidateAsync` with the configuration the command
loaded): `TreeAfter` makes that tree an `OverlaidRegistryFiles` over `TreeRegistryFiles.OfAsync` of the tree the
registry is written into, each path the command writes there read only when the validation asks for it - a fold's from
the agent's files, its merges from the bytes in hand, and what it removes gone - so a fold of hundreds of paths reads the
few the registry cites. Arms the two changed differently (`ArmConflict`, shown under the refusals), a version that cannot
be read as a registry, and a merged text that does not read as one - every problem listed as `check-mutations` words it,
`as the fold would write it` - refuse the path, and `--merge` naming the registry is refused: it is never merged by line.
Run again after a fold that wrote it into one tree only, the merge of its own result gives that result again. What both held is, for a path the agent shares, the bytes kept under the digest
its seed records, used only where they still hash to it; where the seed was written by a build that kept none, the
versions of the path in the main tree's history are read (`ListHeldInHistoryAsync`, at most 64 among the last 512
commits that changed it), each as its commit holds it and then as a checkout writes it, and the first that hashes
to the recorded digest is taken - kept by a fold that goes on to write, so it is looked for once, and never by a dry
run. A version git cannot check out - a filter that fails on it - is passed over and said with the rest: the path is
offered no merge, and nothing else of the fold depends on it. For any other path it is the blob at the agent's base. Nothing is merged against a guess: where neither gives
it, no merge is offered, and the line says why. A fold merges in the main tree's forms, since the same bytes go to
both trees and one digest is then true of both. Whether a file can be run is merged with its content
(`TwoChanges.MergedBit`): the merged bit is the one that differs from what both held, where either side's does. A
bit has two values, so two sides that both changed it changed it alike, and it never conflicts by itself - where
something was held before: a registry both trees made, merged by arm from nothing, whose two copies differ in the bit
is offered no merge (`NoMerge.BitBothMade`), since with nothing before neither bit is a change. What both
held is the seed's record of the bit for a shared path - whichever source gave the bytes, since a kept copy is bytes
under a digest and holds no bit - and the base's entry for any other (`ListEntriesAtAsync`, for those paths alone);
each side's is its file's. Where the platform keeps no such bit, as on Windows, none is read, merged or set. A merged
path is written after the fold's own writes: both trees' copies are asked to be what was weighed - the content, and
the bit where the platform keeps one (`MergedPath.Into`, each `MergeTarget` with what it was when weighed) - before either is written, the main tree's is written first, and the path is recorded as shared, at the merged bytes and bit, only once
both hold it. Stopped between the two, the main tree holds the merge, the agent its own copy and the seed what both
held before; the same command run again merges those again, and since every change of the agent's is already in the
merge, comes to the same bytes. A tree that already holds what the merge gives is not written again.

The rows an agent files (`AgentRows`) are read strictly - a directory for each anchor, a file for each cell, read
as write-anchor reads a cell file - and applied through `IAnchorRegistryService.ApplyAsync`, which composes each
row exactly as write-anchor or set-anchor does, one after another under the registries' one lock: every row is
checked and every refusal named before any is written, a row already as declared is left alone, a change names
only the cells that differ, a row moving between the registries is written destination first, and a write that
fails, or rows that do not read back as declared, put both registries back byte for byte. Every write of a row, alone
or in a batch, is held to what writing a row is (below). A batch makes a row no registry holds only where the request
names it new (`--new` of fold-agent and delete-agent), as write-anchor is only ever asked to make one, and writes a cell
whose stored text does not survive word for word - neither respaced, nor kept whole in an addendum, nor filling an
empty one (`AnchorCellComparison`) - only where the request accepts it (`--accept-lost`); a plan
(`AnchorBatchMode.Plan`, the dry run) shows such a cell word by word, and a check or an apply refuses it, so a fold
refuses before its files are written. Every refusal of a row is collected, so one run names them all. The directories a
fold makes at the top of the tree are handed to the batch (`AnchorBatchRequest.Roots`), so its rows are judged the same
before its files are written and after. The rows a fold applied are recorded as declared
then (`applied-rows.json`), and every row the agent declares is sent with that record (`AnchorBatchRequest.Earlier`):
one it declares as it did then is `Untouched` - never judged, never written, and not among the rows the batch's
others may cite - so a change the registries took since, the orchestrator closing the row or a sibling's
cross-reference, stands. One it declares anew over it is weighed cell by cell inside `ApplyAsync`, under the lock its
write takes, so it is decided against the registries as they are written (`AnchorCellMerge`): each of its five cells -
priority, status, trigger, closing, cross-refs - three ways, the earlier declaration the ancestor. A cell only the
agent changed is written; one only the registries changed is kept, said, and kept again at each fold until the agent
changes it, since the record stays the declaration rather than what the registries held - recording theirs would make
the agent's unchanged file read as its change; one both changed alike is left; one both changed otherwise is shown
with its three texts and written only where `--accept-over` names it, which consents too to what the registries'
text loses. The row the registries are to hold (`AnchorRowOutcome.Effective`) is composed and judged by the same code
as any change, so every rule about a status, a closure or a move between the registries judges it, and the rows read
back are compared with it, not with what was declared. A declared cell is compared with a stored one through one
`AnchorRowCells`, as a row stores it. A row an earlier fold applied that the registries no longer hold is refused, to
be set by hand. The fold never writes the agent's row files: its report tells the agent, once, to write the
registries' text into a kept cell's file before it changes it.

### Deleting an agent

delete-agent measures everything first, under the run lock on the main tree and on the agent's worktree: the
fold and the rows, or with `--discard-uncommitted` what the agent changed. It writes the fold and the rows, proves
nothing is left to fold - the removal's discard of uncommitted work is built from that measurement alone - copies
the agent's Claude transcripts by session id (`ClaudeTranscripts`; found by file name alone, since Claude Code's
format is its own; one not found is said, and one found and not kept stops the deletion before the agent is
closed), and keeps the evidence roots' files, over the roots named before the fold and after it, in the agent's
directory, in a directory named for the run, every copy proved as it is made (`AgentEvidence`, which finds files as a
removal reaches them through `WorktreeEvidence`, the walk delete-worktree's evidence check makes: never through a link,
and a root that is or passes through one, and a link under one, neither kept nor counted). Only then does it record the agent closed: which git worktree it was
(`WorktreeStamp`, the creation and write times of `commondir` in its git directory, which a new worktree at the
same path does not share) and every path it held with its digest. It deletes the evidence originals still holding
what was kept - one that cannot be deleted is named and left, and stops the removal - and asks delete-worktree for
the removal - never forced, its evidence check kept, so a file written after the copy was read back stops it rather
than going with it - then proves the removal: directory gone, git's record gone, no copy recorded on a host. A
question it cannot answer is named as that, never read as nothing left. Once its record says it is deleted, the bytes
kept of what it shared go (`SharedCopies.Remove`): nothing merges from them any more, and they are not evidence, so
delete-orchestrator asks no `--delete-evidence` for them; where they cannot be removed a line says so, and the
deletion stands. An agent made under a worktrees root the
configuration no longer names is refused with nothing touched, since delete-worktree looks under the root the
configuration names.

A closed agent is never folded, seeded or refreshed again. delete-agent run again compares its worktree with what
closing recorded, never with the main tree, which later agents go on changing: a file changed or new since is
work, left for a person (exit 21); a file gone since is the debris of a removal that stopped part way; a path the
main tree ignores is never work, asked of the main tree with its index, since the removal may have deleted the
worktree's own ignore rules first. A directory with no `.git` of its own is never read - git would answer for the
main checkout - and never forced: its evidence is kept again, and the forced removal is named for a person. A
worktree made at the path since is another, and not the agent's to remove.

## Workflow definitions

A workflow is a flow of typed steps that depend on one another by name, declared by name under
`workflows` in `config.json` and defined by a yml file of its own,
`.harness-config/workflows/<name>/<name>.yml` - never an entry in the configuration, which holds the
names and nothing else. The workflow's directory is the workflow's own - its agent files
(`agents/<name>.md`), its prompts, its schemas, the bar it recommends by (`recommendations.md`) -
and every path the definition names is relative to that directory and stays inside it, as an
action's files are relative to its own directory; two workflows never read each other's files. The
name is the folder's, the file's and the `name:` key's alike, one word, and a disagreement among the
three is refused when the file is read, so a copied directory cannot pass as the workflow it was
copied from. `init` writes one - the implementation workflow, from a request to a pull request whose
checks are green, with its agent files, prompts and schemas - and declares it in the config it
creates. Where the configuration declares `implementation`, init writes each of its files that is
missing - the whole set where the repository has no definition of it yet, and where it has one only
the shipped files that definition names, so a file taken out with its step stays out - and keeps
each one that is there, whatever it holds, saying how many it kept: an init that stopped part way is
finished by the next, and a file a repository edited is never written over. A configuration that
does not declare it gets none of it. A file the disk will not write refuses init (13), naming the files written before it, which stay;
and where a file is missing and the repository's own definition does not read, init refuses (12; 13
where it cannot be read) and writes none, since which files it names cannot be told. The shipped set is held to every rule a repository's workflow
is by this build's own tests, so a build whose own workflow does not hold fails before it ships.

A definition declares `inputs` - the values a run is started with, each with a kind, a default and
the options it takes - `params`, its own knobs defaulted in the file and read as `param.<name>`,
and `outputs`, what the whole workflow produces, each from a step of the flow. Its steps are of
one `type` - from `orient` and `reason` through the leg steps, the fold family, the checks, git
and the forge, to `pause` - or groups holding steps of their own: a group runs its steps once for
each work item a `forEach` yields, again and again until a `repeat`'s bound is met, or once as a
block that `gathers` its members' findings into one list. A step raised by another step's ending
says `on` - by the outcome, a failure by exit code - and sends the run back with `then`; the
schedule never starts it. Steps nest at most ten deep; every step's name is a lower-case word,
unique among its siblings and never a word a definition reads for its own.

A definition may declare `recover`: steps written once, which a step gives a failure nothing handles
to by saying `onFailure: recover`, itself or through a group around it. A member that writes none
still hands its failure to its block first, so a block's own handler takes what its members found,
and the hand-over stops at a loop's member and at a top-level step, so a failure never ends a loop's
other items. The recovery's steps read the step that failed as `source` and see every step of the
flow; nothing of the flow reads them, and none of them gives a failure to the recovery. Once they
are done the run goes where `onSuccess` says - `failed-step`, the step that gave the recovery its
failure, which runs again from where it stopped - and a failure of the recovery's own work does what
`onFailure` says once its steps' retries are spent: `pause` or `stop` - never the recovery again, which would be it calling itself. A step that fails again
comes back by its own `onFailure`, until it passes or a person is asked; past `maxVisits` (from 1 to
99; absent, the engine's bound of 99) the run asks `recoveryBound`, with the advisor's recommended
fix. A reasoning step is never recovered, by its own word or a group's: its agent is tried again
(`retries`, waiting `backoffSeconds`, twice as long each time, at most 900) and on each model of its
fallback, an account at its limit or a provider that fails is a pause that resumes it, and what
still fails is asked. The implementation workflow writes `onFailure: recover` on every step of its flow that tests, folds,
checks, commits, pushes or waits - itself or through its group - and `onFailure: pause` on every
reasoning step; its recovery diagnoses the failure (`diagnose`, which goes on with its own conversation across
the visits), asks a person only for what only a person can give (`direction`), and builds, tests,
folds and reviews its fix as a round's work items are.

Every step that runs - every typed step but a `pause` step, which only asks (`StepType.Asks`) - may
say `timeoutMinutes` (`WorkflowStepTypes.Running`): the longest one attempt of it may take, in whole
minutes from 1 to 1,440 (`WorkflowStep.MostTimeoutMinutes`) - absent, its type's own limit where the
type says one (`WorkflowStepTypes.TimeLimitOf`), else none; a group, which runs nothing of its own,
and a `pause` step are refused the key where the file is read, and a `ci-wait`'s is always more than
its `waitMinutes` (absent, `WorkflowStep.DefaultWaitMinutes`). The coordinator counts it with the
engine's clock from each start of the step - an attempt, or its run after a question of its own was
answered (a timer armed at `Start`, disarmed as its runner answers) - and never while the step waits
on a question; a reason step's handoff turn at a pause is bounded by the workflow's top-level
`handoffSeconds`, not by it, and its run at the resume counts anew, while a machine step a pause
leaves running keeps its count. Past it
the step is stopped as a stop stops one - its runner cancelled, a reason step's minutes counted -
and, once its runner has answered, ends `Failed` with 21 whatever it answered, its error saying
`passed its timeoutMinutes (<n>)`: a machine or leg step runs again by the retry table's row for
21, a reasoning step goes to its handlers and its `onFailure`. A runner that does not end within
`PointOfNoReturn.SafePauseDrain` of that stop fails the step all the same, saying so; it goes to
its handlers and its `onFailure` and is not retried - of a step of `others`, the target whose turn
it was fails so, and the step goes on with the next. What that attempt claimed stays held until its
runner answers (`FlowInstance.Lingering`), a stop's drained step's too, so nothing that conflicts with
what the runner may still write starts meanwhile, a later attempt of the step itself among it - each
attempt's claims kept apart, so of two targets whose runners both outlive their turns, each one's
answer gives back its own tree alone. Both
stops share one body (`StopWork`). A pause
leaves a step its limit stopped to end as that stop ends it, with no handoff turn - a failure it
owes while the run is paused waits for the next session - and a stop of the run records it stopped,
as it records every step in flight.

A pre-existing issue - one a reasoning step meets that was there before the run - is reported in the
step's answer (`preExisting`, a member of every shipped reasoning schema but the triage's) and triaged by the step its
`onPreExisting` names: a reason step of the flow's top level that says `raised: preExisting`, which
nothing schedules, and `onAddress: recover`. The validator holds the wiring (V51): the triage is at the top level, waits for nothing, nothing waits for it and no handler sends the run to it, it
takes no `on`, `when` or `always`, sends
a fix to a declared `recover`, and names no triage of its own, which would be it calling itself;
where a workflow has one, every other reasoning step names it. Its agent reproduces the issue on the
base commit and answers `fix` (in a step's way, related to the work, or critical: given to the
recovery, the step it was in the way of running again after), `list` (added, proven, to the run's
`pre-existing-issues.json`, shown in the pull request's body and the handoff, a quote or an output
cut to 4,000 characters, the cut said), `duplicate` (naming in `duplicates` the listed issue it
repeats) or `unproven`; an answer whose course is none of the four, or whose issue to list does not
read back, fails the triage step (12) before it ends. The engine raises it and keeps the list -
though until the reasoning step runner lands no step reports an issue; `issue` is read only by the
triage and the recovery, and `source` by them and by a handler.

`validate-workflow` checks a declared workflow, or every one declared: the file's shape, every key
against the table that owns it (`WorkflowStepTypes`, which the parser reads exactly and `help
workflows` lists from, so what is listed is what is read), every step, input, param and output an
expression or a template names - a step looked up from where the reader is (`FlowScope`) and one
that has ended before the reader runs: never a group's member in the group's own `when`, by bare
name or by path, nor the group around the reader, and the iteration before (`previous`) only
inside a repeat, where a step nested deeper than a bare name reaches is read by its path - and what
each read is: every path typed by what holds it (a reason step's schema through `JsonSchemaSubset`,
the output a built-in type answers, which `WorkflowStepTypes` holds, the engine's facts of a step and
the run's own names, which `WorkflowExpressions` holds), every operator held to what it takes
(`FlowExpression`: `!`, `&&` and `||` a boolean, `==` and `!=` no list and no object, `any` a list
and a test that yields a boolean, `??` and `empty(...)` anything) and every key to what it reads (a
`when`, a test step's `filterWhen`, an `until` and a `blocking` a boolean, a `forEach` and a key that takes a list a list) - a value
refused only where none its reading allows could be what is taken, so that one that may be null is
the run's to read. How the run stands without its `always` steps (`run.status`, `run.done` and the
rest) is read by an `always` step alone; a placeholder stands in a key that is a template and is
refused in any other where the file is read; a pull request's `base` reads nothing but the run's
inputs and `run.name`, one base for every pull request; and a prompt's own placeholders - a reason
step's `prompt` and `resumePrompt`, a pull request's `body` - are read as the step reads, each said at
its line, the iteration before only in a `resumePrompt`. It checks every question
kind an answer names, and every file the
definition points at, beside the definition that names it - a runner or a leg no configuration
declares is said the same way, as is a definition that cannot be read, and a workflow whose
directory leads out of the workflows directory, or whose definition out of its own directory, once
their links are followed. Every problem is collected and reported together (12), without running
anything: a definition that does not hold is configuration that does not, refused with the code
`config.json`, an action file or the parser itself is. A `config.json` that declares no workflow
has nothing to check, and is refused (13), saying that `"workflows": ["implementation"]` declares the one
`init` writes; an empty answer is never read as a pass. The structural rules live in Core
(`WorkflowValidator`), pure, so the engine can run them at the start of a run without reading a
file: the text of the schemas and prompts a definition names is handed to it by whoever reads them -
`validate-workflow` from the repository, and the shipped definition from what this build embeds.
Against a configuration's providers it also checks every reason step, the advisor and the reporter
(V38's config half): the tier is one `tierConfig` defines, the model one a provider lists, the client
reaches the provider, and each model the step may run on is given a level it takes - one level for
every model, or a map of `all` and models' codes, each model's resolved on its own, a code the step
does not run on refused as dead, and a step that goes on with another's work checked against the tier
it inherits. Where the definition holds, the check says each agent's models and the level each runs
at, one line apiece under the line that says it holds, and every key a step's model would read that
no file of the agent clients' slot declares, one beside a key the slot holds among them, with the
steps that would read it - as a step's first model, or as a later model of its tier. What a model
reads is decided in one place (`AgentProviders.KeysRead`) by the client that reaches it, the step's
own or else its provider's: `claude-api` and `glm` sign each request with the provider's key, and
`claude-code` signs in by its entry's `auth` (`AgentAuth`, `AgentProviders.AuthOf`): the machine's
login where nothing is said, reading none, so a step on it is never said to need one; the
provider's keys under `apiKey`; under `oauthToken` or `authToken` the one token the slot holds by the
entry's `token` (`AgentProviders.TokenOf`), appointed by the client beside every provider's keys
and refused where a provider's key is appointed under the same name. Each line is the one sentence a missing key is said in (`AgentKeyStore.Missing`), ending
with the names the slot holds that nothing appoints. It is a report and not a problem: a machine
that only checks a configuration needs no key, and no command of this build refuses one - the run
that will is the engine's, which this build does not yet run. The slot itself
(`Secrets\AgentKeyStore`, beside the host items' own store) reads the git-ignored
`.harness-config/workflows/.env/` by the runner's one rule for a directory of dotenv files - every
file merged, a name declared twice refused in its own words, a file in the directory's place refused
rather than read as none, and (the slot's own shape) each name held to a variable's name, a line
whose name is not one said by its number and never by its text, which may be part of a key - and
from nowhere else, so a stray variable of the tool's own environment neither chooses whose key a run
bills nor redirects one. Where a value is read (`AgentKeyStore.Value`, which no command of this
build calls yet), who may reach the file each name came from is asked by the one rule
the host items' files are held to (`ItemEnvironment.Unprotected`): never a file others can change,
and never one others can read while it holds a secret - a name the configuration appoints as a key,
not a setting such as the program a client starts. `list-agent-providers` says the same configuration without saying one key: each
provider, client and tier, every appointed key and token present or missing and each setting a
client reads present or absent (`AgentProviders.SettingsRead`), exit 0 whatever is missing;
`--template` the sections as `init` writes them, answered before any repository is read - outside
one, and where the configuration it is printed to repair does not load. The Claude Code the clients will drive is npm's own:
`NpmClient` finds npm (a program, or on Windows what `npm.cmd` would have started - the tool never
starts a batch file - with npm and the node that starts it looked for on one PATH by the tool's one
walk of a PATH), `ClaudeCodeInstall` reads the package it holds and the version its program
answers (the version in the line `claude --version` prints, compared as a version, never as
text), and `ClaudeCodeUpdateService` - the second caller of the shared `ProgramUpdate` sequence
`update-tool` has - installs it where npm has none and brings one older than the tool needs up to
the one version it names: never elevated, never beside a running session of that installation
(`ClaudeCodeInstall.SessionsAsync`, the one rule of which processes count: a process whose
program, or a path on whose command line, lies under the package's directory - read as text
first, links followed only for a process called `claude` or `node` whose text did not say, each
path once per reading, off the caller's thread and within ten seconds, a path on a mount that
answers nothing counting its process as one that cannot be told apart -; a `claude` whose
program is node, which is how node running Claude Code shows on Linux and macOS once it has
written its title over its name and line; and a `claude` whose program could not be read,
another user's on Linux - each named in the refusal (13). A `claude` installed any other way
whose program is read does not count, and a table read by names alone refuses the change (13)
by the rule `update-tool` refuses such a wait by, `ProgramUpdate.Unread`), bounded, and read back after a change that
failed as after one that ran, saying what npm holds since. A program the slot names is never
moved, and a slot that cannot give it is refused, never passed over for npm's; the client's
`update` key is read by the service, true where nothing is said. The step runner that asks it
before a run's first step is the engine's, still to come. What an agent of a run may do is one
policy, `Agents\Policy\AgentPolicy`, the same on every client: everything is allowed except the
root actions, the places no agent reaches and the restrictions, which only ever refuse, from every
source at once - `agentPolicy.restrictions` in config.json, a workflow's definitions and its steps -
and never remove one another's. A restriction is one string (`Run(git push *)`, a command prefix
whose trailing `*` is any arguments and the bare command too; `Write(.github/**)` and
`Read(secrets/**)`, path patterns as `GlobPattern` reads them, gitignore's grammar, matched against
the path as it stands under each of the agent's roots, as asked and as resolved so no path reaches
through a link past a rule written for where it lands; and `Web(pastebin.com)`, one domain, the
domain and every name under it, or `Web(*)` the web whole). Built in, not configurable: the root
actions (`sudo`, `su`, `doas`, `pkexec`, `run0`, `gsudo`, `sudoedit`, `runas`, `psexec`,
PowerShell's `-Verb RunAs`, `wsl -u root`, `systemd-run`, `schtasks` with `/ru`, wherever a command
names one), writes outside an agent's writable roots, every read and write of a credential slot or a
Claude Code directory of the run's and every search over one (`AgentPolicy.Search`), and any command
naming one - by `~` or `$HOME`, as a redirect's file or an option's value, against every directory a
`cd` of it reached; no agent starts while the tool itself runs elevated
(`IHostPlatform.IsPrivilegedProcess`, the runtime's own answer): `AgentRunner`, which every client's
launch passes, records such a step `NotStarted` (`AgentNotStartedBecause.Elevated`, in the words of
`AgentPolicy.RefusedWhileElevated`) before it takes a client. A path is decided as asked and as
resolved, its home expanded - and on Windows its device prefix (`\\?\`, `\\.\`, `\??\`, in either
separator) dropped from the text and from what `Path.GetFullPath` makes of it, one naming no drive
and no share undecided, and a shell's spelling of a drive (`/c/x`, `/cygdrive/c/x`, `/mnt/c/x`)
read both as the drive and as the directory it names on the current drive, each decided and the
action allowed only where both are (`AgentPolicy.Spelled`, `AgentPolicy.Plain`); where a link
resolves, where a `cd` moves to and the ground's own directories are spelled the same way, a `cd`
from every place the command reached before it, up to `AgentPolicy.MaxPlaces`, past which the
command is undecided - and a refusal's places compare in any
case on every system, each Windows short name read as its long one (as `Path.GetFullPath` spells it); what a
refusal allows - a writable root, the step's `writes` - compares as the platform does. A command is
read whole before it is matched, `Agents\Policy\CommandAnalysis`: a shell string read once, front to
back, by a recursive-descent reader as the shell's own parser reads it (`CommandAnalysis.Shell.cs`,
its depth bounded at 32) - split on `;`, `&&`, `||`, `|`, `&` and line ends; a comment and a
here-document's lines never commands, a here-document's or here-string's text kept as the input
it gives; the bodies of command, backtick and process substitutions, subshells, functions, loops and
branches read as commands of their own, each substitution's commands before the command it stands
in, whose word keeps the substitution's text, so the word is the expansion it is; a `for`'s or a
`select`'s head kept as a command its reserved word leads, so the paths it hands its body are read,
and a `case`'s patterns never; inside `[[ ]]` its parentheses and operators words of the test; a
redirect kept apart from the words, after them, its operator normalized and before the file it
names, wherever it stood; reserved words read only where a command begins, unquoted; a continued
line joined and `$'...'` decoded. The `NAME=value` words before a program are kept as its
environment and carried to what it runs, and a value that is a program git or another tool runs
(`CommandAnalysis.ProgramValued`: `GIT_SSH_COMMAND`, `GIT_PAGER`, `EDITOR` and their kind) is read as
that program's command line, an `export`'s or a declaration's too; wrappers (`env`, `nohup`, `time`,
`timeout`, `nice`, `xargs`, `command`, `builtin`, `exec`, `eval`, `busybox`, `Invoke-Expression`)
are stripped with their own options, at most 32 deep, what `xargs` reads standing among the words
it runs as an expansion; and it goes into `sh -c` and every other shell's (the words after its
script read as scripts too), a here-string or here-document a shell reads, `cmd /c` and
`powershell -Command` and a decoded `-EncodedCommand` - by any prefix PowerShell takes, behind its
valued options - and the command lines `trap`, `watch`, `script` and `flock` run, each by its own
shell's quoting, to four levels, and into a shell another program runs with its script among its
words. What it cannot read whole it refuses, and the policy denies as `undecided`: a script nested
past four levels, parentheses past 32, a shell that reads its script from its input or from a file
an expansion names (`bash <(curl ...)`), an `xargs` handing a shell its script, a program named by a
variable, a substitution, a pattern or a brace, a brace that spells more than 64 words, a word where
none can stand, and an option of git's or gh's own before its command that this does not know.
Every word of a command is tried as the program of the words after it, a program matched by its
name (`CommandAnalysis.Name`, `.exe`, `.cmd` and `.dll` among the ends folded away), so a command
another program runs meets the rules on its own program; a rule matches through
`AgentRule.Match`, whose `Expanded` - a word the rule reads is one the shell expands - the policy
denies as what the rule may be; git's own options, valued ones with their values, are dropped
before its subcommand, gh's flags before its command's path (`gh pr -R o/r merge` is
`gh pr merge`), and a git configured where it runs - a `-c` or `--config-env` of any key but the
ones that only shape what git prints, `--exec-path`, the `GIT_CONFIG` names, `GIT_EXEC_PATH` and
`GIT_TEMPLATE_DIR` of its environment - is the `git config` it is. A word that names a path is read
as each candidate it may be - the word, a redirect's file, an option's value, the words a script in
it names, each word its braces spell and the path a `file://` address names - and a pattern the
shell expands is refused where it may reach a credential slot or a Claude Code directory; and a command that runs a program which fetches from the network
(`curl`, `wget`, `Invoke-WebRequest` and `Invoke-RestMethod` with `iwr` and `irm`, `http`, `https`,
`gh api`) is decided as a web request too, by each address it names and each host it reaches (a
proxy, curl's `--connect-to` and `--resolve` targets, the proxy of its environment), under the
`Web(...)` rules and `agentPolicy.webDomains`, refused where the step has no web, and refused where
the web is held to any rule when it reads what it reaches from a file. What it does not see is said
in help: the inside of a script file a command runs. The worktree rules
(`agentPolicy.worktreeRules`, on unless turned off) refuse git's state-writing verbs and the merges
and CI starts of `gh` - `gh pr merge`, `gh workflow run`, `gh run rerun`, the `gh api` endpoints and
GraphQL mutations of the same (`AgentPolicy.GhApiEndpoints`, `AgentPolicy.GhApiMutations`) and `gh
alias set` - keep git's own files unwritten whatever `protectedPaths` says (`AgentPolicy.GitsOwnRule`:
a `.git` segment at any depth beneath a root, in any case, so a nested repository's hooks are as
safe as the root's), and hold the tool's own
policy and what the next launch loads unwritten by any agent; its own state
(`HarnessLayout.StatePaths`, the one owner of where that state is) is held so whatever any switch
says, a writable root standing inside it - the agent's own work directory - the one exception; the
tool's own verbs (`AgentPolicy.ToolVerbs`, each the command's own name as its service declares it)
and `run-side-task` are refused, run by any program or from a response file, whatever the switch
says. The protected paths (`agentPolicy.protectedPaths`) are Claude Code's own list written out once
as rules (`AgentPolicy.ProtectedPathRules`), pinned by a test and printed by `help agent-clients`,
refused on every client; a step's `writes` is the one source that narrows, and the reporter's reads
are narrowed to the run's directory alone (`AgentPolicyGround.ReporterRoot`: no command, no write,
no web). For Claude Code the policy is rendered, never re-decided, into the launch's settings
(`Agents\Policy\ClaudeSettings`): an ask rule for every tool the launch has, a read of each
credential slot and Claude Code directory as a deny rule - which Claude Code applies to its searches
too - and the restriction rules as deny rules, absolute once for each root, but where a rule's
pattern there would cover a root the launch writes, each path in the POSIX form Claude Code matches
(`//c/x` for `C:\x`) and a `Write` rule as an `Edit` rule, for Claude Code holds every file tool to
its `Read` and `Edit` rules alone - a second layer, for the engine is asked about
every call and answers from the same policy in its own
process. Where the policy cannot decide a call it denies it, never
allows - nothing runs while the host that would answer is silent or gone. The first client that
asks it is `Agents\ClaudeCode\ClaudeCodeClient`, the `IAgentClient` of `claude-code`: each
launch a new session (`ClaudeLaunch` builds its one argument list and its environment - nothing of
a Claude Code session the tool was started in, the names `ClaudeSettings.Controlled` sets -
`CLAUDE_BASH_MAINTAIN_PROJECT_WORKING_DIR` among them, so every shell command starts in the
launch's directory, where the policy reads its relative paths, never where an earlier command's
`cd` left it - and the one credential an `AgentCredential` gives), streamed both ways and read line by line as
`ClaudeLine`s (every kind the probe's recordings hold), each `can_use_tool` it asks decided by
`ClaudeToolDecider` through the policy, the tools it is given - and what a call of each is of - from
the one table `ClaudeCodeTools` holds; an answer held to a schema checked by `StructuredOutputGate`
and sent back while it does not read; a message to an agent at work, a handoff's interrupt and its
turn (its prompt and its refusal `AgentHandoffSignal`'s, every client's; the agent asked once every
message the engine wrote was taken or dropped - each with an id of its own, by which the
interrupt's receipt names those it dropped), the model each message names, and a key reported
invalid moved past with a new launch, all in one launch's loop that the stored stream
(`AgentRunner`) records as it runs. Once a launch has ended, however it ended, a session file it
left is removed, and what its container could not reach where the runner hands its end back
(`ProcessResult.Uncontained`) is said and kept in the result's `uncontained`; a call the engine's
own deciding fails on is a denial like any other, by the rule `undecided`. The client holds each
launch to the protocol in the same loop: the user's settings read first by `ClaudeUserSettings` (a
credential they would give refuses the run, as a configuration that is wrong; the file and its
digest named in the launch's start, the hooks off), the `initialize` answer bounded, an init said
before any work and its tools and credential source (`ClaudeLaunch.ApiKeySources`) held to the launch's, and every call
that ran (`ClaudeLine.CallResult.Ran`) one the engine was asked about and allowed; a decline is
`Refused` and an account at its limit (`ClaudeLine.RateLimit`, or the turn's error) `RateLimited`
with an `AgentAccountLimit`. The API clients are the next thing the tool grows. This build checks workflow definitions and
starts and drives a run of one (see "Running a workflow" below). Each step is given to the runner of its type
(`FlowStepRunners`, over every `IFlowStepTypeRunner` the root registers - `FlowStepRunners.Served` lists them, and a test
holds the two to each other); a step of a type none serves yet fails, saying no runner serves its type. Those served
are machine steps, each a thin adapter over the owner of its rule: `OrientRunner` reads the main checkout by
`FlowCheckout` - the one probe the run's start reads too, over `IGitClient` and `GitRemotes` - and each fact by its owner
(`LegSelection`, `TestInvocationResolver`, `AnchorCitationService.RootsOf`, `CiSettings.Unset`, `MutationService.Declares`,
`OrchestrationRules.OpenAgents`, `TreeHolders`); `GraphifyCodebaseRunner` checks the graph's longest path against the
run's directory and syncs the run's one codebase graph through `ICodebaseGraphService` (`Core.Codebase`, below);
`PlanCheckRunner` holds the items its `items` yields to `PlanValidator`, whose path budget is
`FlowRunPaths`'s with the deepest file an item owns; `CreateAgentRunner` makes the agent the coordinator named
(`OrchestrationRules.NextAgentName` over the run's agent directories, read under the orchestrator's lock by
`IFlowRecords.NameAgent`, and every name its steps hold) for the model `WorkflowValidator.AgentModels` resolved, both
written to `step.json` before `IAgentService.CreateAsync` is called; run again it reads the agent first and finishes or
drops a making cut part way - one whose hand-over of the main tree's uncommitted state stopped part way
(`SeedRecord.HandOverStopped`) seeded again with the rest - and finishes the removal of one found closed
(`FlowStepResult.NameSpent`); a refusal it meets keeps its cause (`FlowStepResult.Failed`), so a main tree another run
holds is waited out as a held lock is; and `IAgentService.CreateAsync(new AgentCreateRequest(..., NoWorktree: true))`
makes a read-only reason step's agent the same way. Each step is handed the run it belongs to (`FlowRunPlace`) and what the engine read for it
where it starts (`FlowStepRun.Read`: a plan check's `items` and `scope`, the pull request's base `FlowTemplate` fills for
orient, an anchor check's `rows` or `paths` expression, a fold family step's lists of names - read by `FlowStepKeys.NamesOf`
- a `ci-wait`'s commit where it names none - the one the run last pushed), the agent it works in (`FlowStepRun.WorksIn`)
or, of `others`, the target whose turn it is (`FlowStepRun.Target`), and its keys' templates filled (`FlowStepTemplates`, the one
reader of which text each template key holds, the definition's check its other caller). The leg steps - `build`, `test`,
`run`, `check-mutations` - share `LegStepRunner`: the use case of each command (`IBuildLegs`, `ITestLegs`,
`IRunnerLegs` - a run that writes the tree in place, by `HarnessConfig.WritingRunners`, the answer the step's claim of
write reads through `ConfiguredRunnerFacts` - and `IMutationLegs`) run in the agent's worktree or the main checkout, its
legs told to the engine as `LegRunService` says them through the request's `ILegRunWatch` (each leg placed, in a copy or
the tree; each copy made, said where the leg takes its tree after the sync; each leg ended, said by `LegLedger` as its
line is kept), each leg's verdict read on its own and `red` holding the red legs only, with the phase that did not pass
(`LegEntry.TailPhase`, beside `LogTail`); the ledger (`LegRunResult.ToDocument`) and, in an agent's worktree, each red
leg's failing phase log are kept in the step's directory. A leg run something other than its legs ended - refused, or
stopped - ends the step with its own code. The checks that run no legs answer `FlowStepResult.Red` where they found red,
which the coordinator reads by `failOn` as it reads a red leg: `AnchorBalanceStepRunner` and `AnchorCitationsStepRunner`
over the anchor services, held to a step's rows or paths by `AnchorBalanceReport.HeldTo` (each `AnchorFinding` carries
the id of its row, none for a registry as a whole) and `AnchorCitationReport.HeldTo`, their findings the lines the
checks print (`AnchorReports.BalanceFindings`, `AnchorCitationReports.Line`); `CiReadStepRunner` over
`ICiLegsService.CheckAsync`; and `CiWaitStepRunner` over `ICiChecksService`, which reads a commit's check runs and
statuses through `ICiJobSource` until one is red - classified by `ICiLegsService.ClassifyRunAsync`, its job's log read
whole - every one has ended, none appeared, or its time is spent, with `CiConclusions` the one place that says what a
conclusion came to. Every call of `gh` goes through `GhProcess`: the repository it answers for, never asking, ended at its
limit (21) - a step's read at `GhProcess.Limit`, never at the step's time limit, which bounds the attempt and ends
a read in flight with it, so a `ci-wait` whose read stalls reads again at its next poll. The fold family - `FoldStepRunner`, `DeleteAgentStepRunner`, `RefreshStepRunner` and `RebaseStepRunner`, over
`FoldFamilyStepRunner` - calls the verbs' typed use cases and reads their `AgentVerbFacts`, never an exit code:
`FoldStandings.Read` says whether a dry run ends the step, asks or applies; `FlowQuestions.FoldQuestionOf` is the one rule
of the question it raises - `foldConflicts` for conflicting merges alone, `foldOutside` for paths `FoldWithin` finds
outside a fold's `within`, then each consent it lacks, a person's first - with what it is about (`FlowDryRun.Of`, over
`AgentPlanned`), which the runner keeps in the step's `fold.json` (`FlowFoldRecord`) with where its answer will stand; a
consent is given by name only where the dry run made again names exactly that, and one that no longer does is never
passed. A fold's apply, and a deletion's, is held to what its dry run named (`FoldAgentRequest.Expected`,
`DeleteAgentRequest.Expected`, measured again under its hold and compared by `AgentPlanned.DifferenceFrom`) - a
refresh's and a rebase's request carry none - and which steps fold is one rule, `WorkflowStepTypes.Folds`, the
definition's check and the runner alike. `FoldLedger` writes a fold's intent to `folds.json` before the verb is called
and what it left after (`FlowFoldLedgerFile`) - applied, stopped, failed with what the tree holds read, or refused
having read nothing - an intent a crash cut off settled from the tree by the next attempt; the step's `fold.json` takes
what the apply answered before the ledger's settle, and an apply that threw goes on with its own exception, its intent
left for the next attempt where reading the tree fails too. Whether a fold is committed is never written: it is read
from the main tree (`FoldLedger.Uncommitted`, over git's status, read before the repository's section where another
run's ledger changed a path), so a commit made by hand ends a stopped run's hold as the run's own does. A deletion run
again whose agent its record says deleted - its ending cut off by a stop's drain, a time limit or a crash - is done
(`FoldFamilyStepRunner.DoneAlreadyAsync`), its output what `fold.json` kept of the apply. A
refresh or a rebase of `others` is one turn for each target (`FlowInstance.Targets`, `FlowTargetRecord`), in
`OrchestrationRules.AgentNameOrder`, each claimed on its own tree (`FlowClaims.Of`), retried by its own count and ended
on its own. The other step runners - the agents, the rest of the steps over the services the tool already has - are
the next thing the tool grows.

A definition is held to the `config.json` it is checked under as well: a `run` step whose runner -
or a runner its run checks name - says `writesTree` names with `in` the create-agent step whose
worktree it works in, since the run reads its definition in the main tree and folds into it; the
agents a run makes, named `a` and a number that grows to the workflow's `maxInstances`, fit
`worktrees.maxNameLength`; and a commit step gives a value for every variable `commit.template`
requires and none for one it does not declare - or, with no template, a `message` and no
`variables`. Within the definition, the `amend` block takes an amended request into a text
`input`, a changed answer (`changedAnswerMinutes`) or both; its `step` is one step, outside every
`forEach` and `always` group, raised by an update alone and going on with the work of a step beside
it; `whenAddScope` names a step declared directly in a `forEach` group - the work-item group - and
`whenChangeScope` and `whenRemoveScope` that step or alternatives of its type beside it, which say
no wiring of their own and which no other step names; and the work-item group expands from the
`items` of a `validate-plan` step it waits for. A step that names a create-agent step with `in` or
`of` waits for it - through its own `dependsOn`, a group's around it, a handler's source or the step
it is an alternative to, a step inside an alternative group among them (`FlowGraph.Waits`) - since it starts once the
agent is made; where that step ended otherwise than done - skipped, passed over, failed under `always` or handled - the
coordinator does not run it (`FlowNotRunCause.AgentNotMade`), saying the agent it works in was never made, and the agent
it works on is kept on its instance (`FlowInstance.WorksIn`) from its start, so a deletion's record names the agent it
deleted after its create-agent made another. A template is
read by the configuration's one brace grammar (`FlowTemplate.Pieces`): `{{` and `}}` are braces,
`${...}` is left as written, and a placeholder that names nothing is refused. `piece` is read only in the work-item group, and what only a
prompt reads - `recommendations`, `question`, `status`, `update`, a skill - by no step's own key.
What only a prompt reads stands where its agent is asked it: `{recommendations}` in a reason step's
prompt and the advisor's, `{question.*}` in the advisor's alone, `{status.*}` in the reporter's two -
`{status.question}` in its `askPrompt` alone - `{update.*}` in the prompts of the step that plans an
update and of the steps of its scope, and `{piece.*}` in a step of the work-item group; the
advisor's and the reporter's prompts are read too, and read no step by name, and a pull request's
`body`, which the engine fills and no agent reads, reads what a template key reads and no skill.
A question gives only the texts the engine names (`step`, `kind`, `problem`, `answers`, `record`),
and so do the status (`facts`, `question`, `records`), a plan update (`cause`, `since`, `work`) and
a piece (`kind`, `changed`, `keep`, `undo`, `instead`, `cause`, `before`) - each list kept once, in
`WorkflowExpressions` beside the run's own texts, and read by the typing; the bar is one text, and so
is each run fact (`WorkflowExpressions.RunFacts`), which a template reads and an expression does not.
A `pushCheckpoints` with no push step that is a checkpoint, two handlers one ending raises that
both name a `then`, a fallback that is its agent's own model or is named twice, a gathered name
given twice, named for an engine's fact or for a member, `times` or `then` beside a `pause`
answer, and an advisor whose `kinds` are given empty or name `ask` or `runBudget` are each refused.

`export-workflow <workflow> --to <dir>` is a use case of Core (`WorkflowExportService`): it copies
a workflow - its yml and every file it names, the one list `validate-workflow` and `init` read
(`WorkflowFiles`), and no other - into `<dir>/<workflow>/`, a folder of its own that must not be
there (13 where it is). A declared workflow is exported once its check holds (12 where it does
not), from the definition and the directory that check found, so what is copied is what was
checked, each file byte for byte and proved where it lands (`VerifiedFileCopy`); with `--shipped`
it is the implementation workflow as this build ships it, held to `WorkflowValidator`'s rules as for a
configuration with no `commit.template`, written as
`init` writes it. The yml is written last, and a file that cannot be written takes back what was
written before it, with every folder the export made, so a folder with part of a workflow in it is never
left behind - or the refusal says why it could not be taken back.

### Running a workflow

A run is an orchestrator the engine drives (`FlowService`, a use case of Core; the CLI only wires
it). `run-orchestrator <workflow>` checks the definition as `validate-workflow` does (12 where it
does not hold) - read from the definition tree, the main checkout, whichever tree the command was
typed in, as every later session of the run reads it - reads `--input` - `name=@<file>` through `IFileSystem`, relative to `--directory`,
a missing file a usage error - and checks the inputs against the definition before anything is
made, by the one owner of what an input takes (`FlowInputs.Resolve`, which the engine holds its run
to as well); then, inside the repository's one section (`FlowSessions.AcrossRuns`), it refuses another
unfinished run with work in the tree - a run whose agent's record cannot be read counts as one,
said - chooses the name (`r<N>`, one more than the highest there; a name `--run` gives is held to
`worktrees.maxNameLength` and refused where a record or anything else is there), holds the start
to the path budget of its agents' worktrees with the copies and workers its leg steps keep inside
them (`FlowRunPaths`), makes the run's record, its status and its owner
(`.orchestrators/<run>.owner.json`), and leaves the section. A session of a run already made -
`resume-orchestrator`, or `answer-orchestrator` where no engine runs the run - first sweeps what a
killed engine's steps left running (`ChildSessions`), refuses a definition the repository's entry
in the main checkout no longer matches - compared by digest of what the definition says
(`FlowRunContext.DigestOf`), never of where its file was read from, so a checkout that moved or a
command typed in another tree is no change - and claims the run inside the same section: refused by
name where the run was deleted since, refused while a live engine owns it, a dead owner reclaimed
and said, a request no engine took removed. Two starts, or two sessions of one run, can never both
pass; and `delete-orchestrator` decides and removes inside that section too, taken before the
orchestrator's own, so no claim comes between its look at the owner and its removal of the run.

The run's records are written in one order at every change - a step's own files (its `handoff.md`
where it handed off, its `output.json`, then the `step.json` that names them - a refresh's or a rebase's of `others`
with each target and where it stands), then `flow.json` (the
truth a resume reads), `workflow.json`, `pre-existing-issues.json` where the change listed one, and last the event
in `events.jsonl` - each read back strictly: a value the engine does not name, a nested record that
does not hold, a time in any but the one form (`yyyy-MM-dd HH:mm:ss`, UTC; one not yet come is
written `null`), a log line out of sequence or corrupt is refused by its number, and only a torn last
line is set aside, said. A resume reconciles `flow.json` against every step's own record, taking the
step's own whole wherever there is one, keeping the index's order and adopting what only a step's
directory holds at the end. The graph is rebuilt in that order, never sorted again - `build+10` after
`build+2`, `rounds#10` after `rounds#2`, a loop's later visit (`rounds+2`) and its items as their own,
a block's members whatever the block ended, the agents the run holds open (each a create-agent step
made, held by its newest visit, that no deletion deleted) - and a record whose scope never comes, or
whose state the engine does not name, is refused rather than taken for one that runs again. It takes back a handoff a
crash left beside its record - the engine's where its first line says so and why, as the engine
writes every handoff of its own (`FlowHandoff.ByEngine`, read back by `FlowHandoffRecord.Of`), the
agent's otherwise - reads each handoff back as it was handed, and refuses - naming the file - a record
that names a handoff that is gone, and a missing `questions.json` or `pre-existing-issues.json`,
never made again empty. A handoff an attempt still held when it was cut off - an agent's account no
prompt holds yet, stopped or running - is given to the next attempt; neither a later pause whose turn
gives none nor the resume writes the engine's over it.

The session then goes on from those records alone, where the run stood, whatever ended the last one
and whichever engine wrote them. Every question in `questions.json` comes back with every answer it
was given - the next id after the highest of any state, and what a configuration's counted answers
spent and every `more` a bound was granted counted again from the answers - and a step waits on each
one still open. What was running or stopped runs again as a new attempt, and so does a failure that
ended the run (its record's `failure` is `endedRun`) save one an answer `fail` ended, which stands and
keeps the run stopped; what that stop left NotRun (`notRunCause` `RunEnded`, or `DependencyFailed`
where the failure no longer blocks it) is drawn again under the same id, and a group stands open
over such a member, never ended done over it. What the run owed is made before anything runs: a
failure recorded `owed` - one that ended while a pause recorded what it left running, or before what
decides it was written - an ending whose handlers its record holds `owed`, issues a step's record
holds owed their triage (`issuesOwed`), an issue a triage answered fix for whose recovery was never
raised, and every redirect in `flow.json`'s `redirects`, each in its turn - a pause and a hard stop
alike leave a redirect owed, never making it, for the resume makes it once what they stopped stands
to be withdrawn. A triage's answer is
listed and counted before its end is recorded, so the end's one write carries the run's file of
issues (`FlowSnapshot.Issues`) in the one order; a crash before that file leaves the end written, and
the resume lists the issue from the triage's answer - once, for one the list already holds the same in
all but its id and time is not listed again. Each record is written before what follows from it - a question before the record of the
step that waits on it, an answer before what it changes, a visit with what raised it, a failure
owed until it is decided, every visit a redirect leaves marked `leftBy` before the redirect leaves
the run's records, and a handler a redirect leaves before it ended marked `left` in its source's
record, for it took nothing: the ending it was raised for is abandoned, settled by none of its handlers
and never rebuilt by a resume - so a crash between any two writes leaves records the next session completes: an
answer written is carried out once, a configured answer never written is given, a handler is never
raised twice, and an ending whose handlers all ended is settled. A session is never said done over a
step that never ended, nor one its scope never drew. What the session's start writes is held, in the
order made and each with the run as it stood, until it is known the session goes on: a resume beside
an open question is refused only where nothing can run until a question is answered, and then it has
written nothing - no event, no attempt, no state; a session an answer began, and one whose budget
question is open, are never refused, and end paused on the question left. A stop asked between
sessions - `abort` answered with no engine running the run - ends the session it begins where the run
stands, stopped, as a stop in a session ends it: nothing it found stopped runs again or is given the
engine's handoff in place of its own, nothing owed is made and nothing starts; the session after it
goes on with all of it.

Each session ends however it ends with the run's report (`report.md`, `report.json`), its owner
released in a `finally`, its line in the run's log, and every record it wrote named in text and in
`--json` (`FlowRunResult`, `FlowReports.Document`): `0` done, `20` failed - `21` where every failure
that ended it was incomplete after its retries (`FlowRetry.RunsAgain`, read from each step's record:
its code, and the `cause` it keeps where the code cannot say it, `LockHeld` for a refusal whose only
obstacle was a held lock) - `1` paused on a question,
the budget's among them, and `130` at a safe pause or a stop. A refusal met inside a session is
answered as a refusal, naming the records as they stand. Another terminal reaches the engine
through two files its poller reads at once and every 2 s: `stop.json` (`pause-orchestrator`, which
waits until the pause is complete; `stop-orchestrator`, which waits 30 s) and `inbox.json`
(`answer-orchestrator` to a running engine, each answer under an id of its own). The poller never
waits on what it hands the engine; what it cannot read is said once and read again, anything else
asks the run to pause; a last poll at the session's end takes what came late, and the next session
takes what is left. The engine takes an answer offered before a session begins at its start, before
anything is admitted - so a resume is never refused over the question it settles - and hands one left
unread as its session ends back cancelled, never refused, so it stays in the inbox for whoever takes it
next; what a step spent that came late is counted. Every answer handed over is weighed by one owner
(`FlowCoordinator.AnswerHandedOver`), whether a session's loop or the inbox taken between two drives
meets it, and what became of it is written into `questions.json` under its inbox entry's id before
anything else is said of it: an answer taken carries the id in who gave it (`by.inbox`), and one the
run does not take - its question no longer open, or it changes an answer already given - is kept in
the file's `refused` list, then recorded as an `answer` event naming why. An answer a session weighed
leaves the inbox only once what became of it is written: one weighed at a session's start, whose writes
are held until it is known the session goes on, is settled when they are made, and handed back - left in
the inbox, and said - where they never are, a refused resume writing nothing; so nothing empties from
the inbox an answer the run's records do not hold. Once settled it is counted, and leaves the inbox
once the file lets it go: a removal that fails is tried again at the next poll or drive's end, and one
the inbox still holds as the session ends - the removal failed to the last, or a crash came between the
take and the removal - is settled by the run's records, so a later session that meets the entry only
empties it from the inbox: no answer is weighed, offered or taken twice. Where the engine leaves before it takes a request, the command answers from the
run's state and takes the request back. `stop-orchestrator --now` ends the engine's process tree -
only on this machine, and only the process whose start matches the one its owner recorded - and
removes the owner once it is gone. Whether a run's engine is live is asked of one owner
(`FlowRunOwner`) by every command: `delete-orchestrator` refuses a run a live engine owns (13,
naming `pause-orchestrator` and `stop-orchestrator`) and removes a gone engine's owner with the run,
and `list-orchestrator` says what the engine is doing - none, running, pausing, recording what a
complete pause left running, or gone.

A session's interrupts are its own (`FlowTerminal`, `FlowInterrupts`): Ctrl+C, and off Windows
SIGTERM and a hang-up, are taken by `PosixSignalRegistration` and never end the process, and the
command line's own termination handling is turned off for the three session verbs
(`PointOfNoReturn.TerminationTimeoutFor`, the one choice of the termination timeout, which
`Program.cs` makes by), since it cancels at the first interrupt and starts a
clock. They are counted once, for the whole session - every drive of it, the one an answer taken as
it ended begins among them - and the terminal's bound begins at the second of that one count,
whoever pressed the first; only the session's end clears it. The first asks the engine for the safe
pause, which starts no clock; the second asks it to stop where it stands: the engine records a step whose runner does not end within 90 s of applying
the stop as stopped all the same (`PointOfNoReturn.SafePauseDrain`), and the command ends within 120 s
of the interrupt whatever is left (`PointOfNoReturn.HardStopBound`, the drain and 30 s to write the
end, so the terminal never cuts off a session the engine is still ending in order) - what a crash
leaves, which a resume goes on from. A session cut off answers as every command over its run does:
the run and `resume-orchestrator <run>` named, every record listed in text and `--json`, and its line
in the run's log. A hang-up keeps the process to pause the run but leaves nobody to read the
terminal: from it on the terminal says nothing, and a line said while the session runs - an
interrupt's, the poller's (`FlowTerminal.Warn`) - is dropped where the terminal can no longer be
written to, never an exception that ends the process part way; the run's records hold what happened. The engine's loop alone applies either,
and a cancellation of the session's token is the second, never a cut-off. A step of a type no
runner serves yet is given to `NoStepRunner`, `FlowStepRunners`' fallback: it fails, saying no
runner serves its type (its code 14).

### The codebase graph

A run's `graphify-codebase` step keeps a map of the repository under the run's `graph/` (`Core.Codebase`): every file the
tree lists is a node, every declaration an implementation reads is one, and the directed edges between them each say how
sure they are (`exact`, `unique`, `candidates`). Nothing of it names a language. Everything that reads a file's contents
is an implementation of one contract, `ICodeGraphExtractor` - its `Language`, its `Version`, its `Claims`, whether it
`Outlines` its files rather than reading a language's declarations, a pure `Extract` and its `Help` - which answers a
language-neutral `FileExtract`: nodes - a value's type (`ValueType`) and the type an extension extends (`Extends`)
among what they say - the scopes they are put in (open to
other files or closed: what a closed one declares, and the type or scope it is the scope of, its file's alone), imports by scope, relative path, address or path ending, with their prefixes and filters,
address roots, references by name with their qualifiers, and four marks - a prototype, a test, generated code with
why, and each line it saw something it cannot bind or stopped reading at (`dynamic`) - of which the linker reads the
first two. An implementation is registered by being written:
`CodeGraphExtractors.Implementations` is the one scan of the core, which `AddDssHarness` registers each type of, and the
registry built from them refuses two of one name, two claims of one exact name or extension of which either gives no
content test, no fallback or two, a fallback that claims a name or an extension, a claimed name no file can bear or
extension no file has, and a version below 1 - so a clash is a red test, never an order. A file goes to the
claimants of its exact name, else of its extension, else to the fallback (`listed`); their content tests decide among
several, and none or several saying yes lists it `ambiguous`. Everything else about the graph - `codebaseGraph.languages`,
V52's names and `help codebase-graph`'s table - is read from the registry. A test keeps the core from mentioning
anything under `Codebase\Languages\`. Every span counts lines as `CodeFileText.Lines` cuts them - CR LF, LF and CR
alone, as an editor and git count - and an implementation that reads a parser's positions turns each into a line by
`CodeFileText.LineIndex`, since a parser may also break lines at NEL and the Unicode separators. An extract is sound by `FileExtract.Problem` - every span inside the file, every
parent present, holding its child and no cycle, ids unique and of its implementation, text whole UTF-16 - and every
node's digest is the core's to set (`WithDigests`), whatever the implementation set. An `Extract` that throws or answers
no sound extract, or a content test that throws, lists its one file `unreadable` with why - a defect `failOn: incomplete`
fails on as it does on any file not read - never the end of a sync. Each implementation's tests derive
from one kit, `CodeGraphExtractorContractTests<T>`, whose checks (`CodeGraphContract`, each shown red on an
implementation that breaks it) hold it to fixtures for each claim, determinism whatever the line endings, answers alike
from many threads, a sound model, tolerance of any cut of a fixture, pure content tests that never both say yes with
another's, the extracts stored with the tests for its version (`fixtures/codebase-graph/<language>/v<version>/`,
written where missing with `DSSHARNESS_STORE_EXTRACTS=1`), and the edges its fixtures name through the linker.

The one linker (`CodeGraphLinker`) resolves every reference by the same steps for every language - the qualifier's
scope, a type and its bases among it; the enclosing scopes from the inside out; a scope import's prefix (an alias),
for what that import stands for; the file's imports in order, filters applied; the repository by name and arity - a
qualifier being read as such a prefix too where no type or scope has it as its whole qualified name, from the file
that makes the import or, made global, from every file of its language - and derives `dispatches`, `declares` and `tests` from the `implements` edges
and the marks. A qualified reference is looked for where its qualifier says alone. A `type` qualifier is a type or
scope found as a name is - nearest first in the scopes it is written in, then the container of its whole qualified
name, an alias, an import's, the repository's - else every container its name ends; a `named` one is that too, unless
a value a scope it is written in holds (a base's among them) is nearer. A value - and a `value` qualifier - is
followed through its member's `ValueType`, each name of a chain, from a value or from a type, through the one before's
type, a type the repository does not declare holding nothing it declares; a call on a value of no said type goes by
its name in the repository, never to a type and never to the caller's own scopes. A call a type does not hold goes,
`unique`, to a member that `Extends` that type or a base of it, as the extension's own file names the type - or a type
the repository lacks, by its written name - where the call's file reaches it: the scope of the extension's type, or
the one holding that, is one the call is written in or its file imports, or is the top, or the call's file imports the
extension's by path; one in a closed scope is reached from its own file alone, and no override is an extension. Extracts keep names, not targets, so a name a changed file adds
binds callers in files that did not change. Each finding keeps every key it read - a name's declarations, an id's parts,
a qualified name's containers, a name's run's end, a scope's parent, a type's bases, a file's imports and open scopes,
a language's global imports, the address roots, a path, a file name - each read through the one accessor of its index,
which says the key (`Named`, `ScopeNamed`, `ContainersOf`, `ParentOf`, `PartsOf`, `BasesOf`, `ImportsOf`, `IsFile` and
the rest); `CodeGraphLinker.Relink` takes the link it is made from (`CodeGraphLinked.Memory`, held in memory alone)
and finds again only the references of the files not given as the very object that link was given, and those whose
finding read a key whose part - what each changed file holds of it - is not what it was, the bases each pass found moved
among them; every other reference keeps its finding, and `References` counts what was found. A global import is read from the file that makes it, so a relative one
names the same file from every file of its language. An interface's member dispatches, for each type that implements it -
itself or through a base, the interface itself or one extending it - to the member with a body that answers it: the type's
own, else its nearest base's it inherits or mixes in, never an interface's own, which has none. A dispatch is as sure as
the least sure edge on its way up, each of several bases that answer alike is one of the candidates (none past
`CodeConfidences.MostCandidates`), and an edge found more than one way is listed once, as sure as its surest finding.
A linker that answers otherwise raises `CodeGraphLinker.Revision`,
which every link result names: a graph linked by another revision is linked again, though no file changed and none is
read again.

A sync (`ICodebaseGraphService.SyncAsync`) lists the main checkout by three calls of `IGitClient` that take no lock -
`ListIndexAsync` (`ls-files --stage -v -z`, the one reading of the index the whole tool shares), `ListWorkingFilesAsync`
(`status --porcelain=v2 -z --untracked-files=all --no-renames`) and `HashWorkingFilesAsync` (`hash-object`, its paths on
standard input 200 at a time); a listing of given paths names them as arguments, in batches no command line outgrows
(`CommandLineBatches`: 200 paths and 8,000 characters at most) - leaves out the credential slots and the
tool's state paths under the run's worktrees root before anything reads, and holds every file to the core's rules
(`CodeFileText`: a NUL among the first 8,000 bytes - `BinaryContent.Holds`, git's reading rule and its window, which
the citation scan asks too, where the line-ending rewrite asks git's conversion rule, `BinaryContent.ForConversion`, of a
file declared `text=auto` alone, as git does -
or a Git LFS pointer is binary, UTF-8 or UTF-16 by its mark is read,
anything else is unreadable) before its implementation does. A path whose file git does not look at - marked
skip-worktree, as a sparse checkout marks it, or assume-unchanged - is hashed and read where its file is there and left
out where it is not; a repository inside the tree that git lists whole as untracked is not entered; a file about to be
hashed - changed, untracked or not looked at by git at a listing, moved at a check - that is a link, or that a link lies
along (`LinkPaths.LeadsElsewhere`, a directory junction git walks into among them), is listed a link and neither hashed
nor read, by the one look both take. Each file read is
hashed once more, in one batch, once the reads are done: one whose blob moved, or that went, while it was read is listed
unreadable and keeps nothing, so an extract is never stored under a blob its bytes were not. It reads a file again only
where its blob, its claimant, its claimant's version, or what `maxFileBytes` or `languages` decide of it changed, or
where its extract is gone, and tries a file it could not read again at every sync; a run with no graph starts from the
newest earlier run's manifest
that reads, its extracts copied as they are reused and a file whose extract that run lacks read again. Its records go
through `OrchestrationStore`, each strict with its `Problem()` and placed by `OrchestratorLayout`: `graph.json`, the
manifest with its digest; `symbols.jsonl`, the link result naming the manifest's digest and the linker's revision, made
again where either does not match - as lines, its head counting what follows and then each node, edge and count on one
of its own, since it grows with the repository and a list read or written whole is held whole while it is
(`JsonLineWriter`, `JsonLineReader`); `extracts/<ab>/<blob>-<path>.<language>-<version>.json`, written once, before the manifest that names them;
`views/<agent>.json`, an agent's view (`CodeGraphView`); and `sync.jsonl`, one line for each sync - what it listed,
hashed, read, reused and removed, the references it found, how many files it could not read (`Unreadable`) and, for one
that failed or a view a moved base could not be held against, why (`Failed`); the store's appends
to it take turns within the process. An extract is named by its path too: an implementation's answer may read the path - its generated
names, its test directories, a file's own names - so a renamed file is read again. The last sync to end with none under
way removes each extract neither the manifest nor a view names, where one may have been left: at a process's first
sync, after a record was written that no longer names an extract the one it replaced named - or in place of one the
sync did not hold, a view's first, a seeded base, a rebuild - after a sync that did not end, and after a view was
dropped - a sync that changed nothing does not look. A sync that failed, not one stopped, also leaves the process
holding no tree of the graph, so the next sync takes each tree as its records say (`CheckAsync` syncs it first, as it
does a tree this process holds neither a link nor a link result of). A record that does not read is refused, its reason
kept: a whole sync builds it again, saying why, and a sync by paths takes nothing and says why; one that could not be read
at all fails the sync. Extraction runs on one of every
`CodebaseGraphService.ReadShare` (2) of the processors.

An agent's view is its worktree listed the same way and held against the base - the main checkout's manifest, synced
first where the run has none - as the overlay of each file the worktree holds otherwise and each of the base's it does
not hold, the digest of the base it stands on with it: a file whose extract the base or an earlier sync of the view made
is not read. A `graphify-codebase` step with `in` syncs that agent's view, or the base for an agent that only reads. Each
tree has its gate: syncs of one wait for each other, syncs of different trees run side by side, and a view takes the base
as one snapshot, which a view reads without the base's gate and never puts back older than a sync left it
(`Graph.Adopt`). When the base moves, every view - one whose first sync is under way among them - is held against it
again under its gate with no file read, from the earlier manifest it stood on, until a newer sync of the base holds them
all, so no view is put back on an older base; a view that could not be held is said - a line of `sync.jsonl` and a
warning - and made again by its next whole sync, the base's sync standing. A view's first sync makes the run's first base
before it takes its own gate, and a sync that waited for the base's gate for that makes none where another made it
meanwhile: no gate is held while another view's is waited for, so no two syncs wait on each other. `SyncPathsAsync`
brings a tree up to date by the paths given alone, git asked of those paths (`--literal-pathspecs`), with no listing of
the rest; `CheckAsync` is the check an answer of the graph makes of the files it names before it is given - no command
answers from the graph yet - a `stat` against what the view recorded, or against what an earlier check found of a file
whose time alone moved, trusted only where that time is older than the tree's last whole sync's record (`Tree.Written`),
as git trusts an index entry older than its index; a hash only of a file either moved or whose time is not older, and a
file whose blob moved read again, its tree linked again and written, and each extract the answer names read, before the
answer is made - no sweep comes between - and a tree this process holds neither a link nor a link result of synced
first, so the answer is its own link's; `DropViewAsync` takes a view out. What a process linked of each tree is held in memory,
with the base's link result, and the next sync links again from it (`Relink`) - or keeps it, unread and unwritten,
where nothing changed; a process begun again links its first sync whole, from the extracts.

The engine syncs on its own, through `FlowGraphSyncs`, only where the run has a graph and under the claim its step already
holds: `FlowStepRunners` syncs a reason step's view where it starts - its agent's, the one the step it goes on with
works in (`FlowCoordinator.WorkingOf`, which its claims are read through too), the base for one that reads; `fold` and
`delete-agent` sync the base by every path the run's folds changed that the graph has not taken yet
(`FoldLedger.Ungraphed`, `FoldLedger.Changed`) - before each attempt does anything and after its apply - and record each
fold it took (`graphed` in `folds.json`), so a sync that failed, was stopped or was cut off by a crash is owed until one
goes through - a graph whose manifest does not read takes none of them, which is said with why, and they stay owed until a
whole sync builds it again; `refresh` and `rebase` sync their target's view where it has one; every one of these first drops the view
of each agent that is no longer live, `delete-agent`'s own after its deletion among them, so a drop that failed is tried
again. A sync of these that fails, for any cause but a stop, is said - a line of `sync.jsonl` that says why, where it
names the view, and a warning that says what takes it up again - and never fails its step - nor does a reason step's repository that does not read where
the hook looks for it, which its runner finds as it does.
`delete-orchestrator` removes the graph with its run, outside what `--delete-evidence` guards.

## Anchor registries

An anchor is a named piece of deferred work, kept as one row of a markdown registry. Two
registries hold every anchor, and each anchor lives in exactly one of them:
`anchors.pendingAnchorsPath` holds the live anchors, and `anchors.doneAnchorsPath` is the
archive of closed ones. `init` creates each registry that is missing from a skeleton
embedded in the tool, and never touches one that exists.

### One table, six cells, one verdict

A registry is an introduction and exactly one anchor table, recognised by its header row,
`| Anchor | Priority | Status | Trigger | Closing work | Cross-refs |`. A file with no
anchor table or a second one, or with an anchor row outside the table or inside some other
table, is malformed: where a new row belongs would be a guess, and a stray row is counted by
nothing. Every command that reads or changes anchors refuses a malformed registry (exit 20)
rather than answer from rows it cannot trust, while `read-anchors --lint` and
`check-anchor-balance` report the problem among their findings (exit 1). A directory where a
registry's file belongs, or a file that cannot be read, is refused by every command, `init`
included (exit 20): taken for no registry, a directory sent its reader to `init`, which then failed
to write over it.

The Status cell is the only verdict that decides: `🟠 OPEN`, `⏳ GATED`, `🔵 DISCLOSED` or `✅ CLOSED`. A
row is closed exactly when its Status cell starts with ✅, and every other glyph, including
one nobody anticipated, reads as open: a row wrongly read as open stays visible as work,
while a row wrongly read as closed disappears from every count. Nothing is inferred from
the prose cells to decide a verdict or a count. A registry whose rows open a closed Trigger
with the closure itself can say
so with `anchors.triggerCarriesVerdict`: a closed row's Trigger then opens with ✅ and no
other row's does, the writing commands refuse a row whose two cells disagree, and the lint
and the balance report one, so that a row states its verdict once, even where it states it
twice. A closed row's Trigger may open with ✅ and then 🧾 instead, the bookkeeping pair, when
its closure only repairs the mark of work done before the change that closes it (see the
balance, below). Whatever the setting, a closed row whose Trigger opens with the two marks where
they are not read as the pair - the setting is off, or emphasis stands between them - is noted,
failing nothing, since only its writer knows whether they meant it: by the lint wherever it is, and
by the balance where it is a closure the change made.

### Writing a row

Commands take fields, never rows. Line breaks collapse and pipes are escaped, because a raw
`|` adds a column and shifts every later cell, and a wrapped row hides its id from every
search. Only the breaks go - each, with the whitespace either side of it, as one space, at
every boundary a reader of the file might split a line at - and so does whitespace at the
value's very start and end; every other character is kept as given: a run of spaces or a tab
inside a line is often a cell's evidence, quoted tool output or aligned figures. A value that already holds an escaped pipe is refused: escaping it
again would double the backslash, and it usually means someone copied a raw table line. A
cell given in a file is read as UTF-8, and a file that is not, or that opens with a byte-order
mark, is refused by name rather than cleaned. Every composed row is read back through the same
parser before anything is written.

A value the door would store cut is refused (`AnchorCellCuts`, exit 10): an id cut where a line ends, found as
check-anchor-citations finds one (`AnchorIdScanner`: at a hyphen that ends the line, or where the next line carries it
on into a row's id); an id with a space after one of its hyphens, across the segments a citation carries; a path's
directory ending a line before a file's name; and a path with a space after a `/` before a file's name, where it starts at
a directory at the top of the tree - read as the write runs. A value that newly cites an id no row holds is refused
too (exit 13): every id a new row cites, and every id a changed cell cites that its stored text did not, read as
check-anchor-citations reads a citation, the ids of the batch a row is applied in resolving too. A cut or a citation the
stored cell already held is history, and is not judged again.

`set-anchor` rebuilds only the cells it was given and writes every other cell back byte for
byte; a row whose cell count is wrong is refused rather than guessed at. A new id must match
the minting rule (`D-` and at least three segments by default), while an id already in a
registry only has to be well formed and is never rewritten.

### Where a row goes

The destination is derived from the status, never named by a caller: a closed anchor goes to
the done registry and every other status to the pending one, so a live anchor can never be
filed where nothing reads it as work. New rows are appended at the end of the table; tables
are never sorted.

A move is two file writes and cannot be atomic. The destination is written first and the
source second, so an interruption leaves the row in both registries, where the next change
refuses the duplicate loudly, and never in neither, which every count would read as a
closure. Each file is replaced whole through a rename, so a reader never sees a torn file.

### Which copy, and who may write

A registry git tracks is read and changed in the tree the command runs in, so the change
travels with that branch. A registry git ignores has no copy in a worktree, and is resolved
in the main checkout, where ignored harness state always lives. `init` resolves registries
the same way, so one it creates from inside a worktree is where every later command looks.

Every change holds a machine-wide named mutex, keyed by the two registry paths, for the whole
of its read, decide and write. .NET supports named mutexes on Windows, Linux and macOS alike,
and named semaphores on Windows only. A mutex must be released by the thread that took it, so
the locked work is synchronous by construction. A change that cannot take the lock within 10
seconds writes nothing and exits 13. Those seconds, like every wait for a machine-wide lock's,
are kept by a clock that never steps: Linux ends a wait for a named mutex at a deadline on the
wall clock, which a WSL clock stepping forward by 24.8 seconds passes at once, so a wait half a
second old ended as though the whole window had passed. A read takes the lock too, for as long
as reading the two files takes, and one that cannot take it in time reads nothing and exits 13
as well. A lock that belongs to another user refuses the same way (exit 13), and one the system
will not open stops the command (exit 15). One file needs no lock, since every write replaces a
whole file in one rename. Two do: a move never leaves its row in neither file, but a read of the
destination before the move's first write and of the source after its second finds it in
neither, which reads as an anchor closed or lost, and a read the other way round finds it in
both, a duplicate.

### The balance

`check-anchor-balance` compares the working tree with a base commit (`--base`, default
`HEAD`), measured from where HEAD's history left the base's (`git merge-base`), as
`check-anchor-citations --current-pr` measures a branch: the base itself where it is an
ancestor of HEAD. Compared with directly, a base that had moved on counted its own later
changes, reversed, as the change's - an anchor it closed as one the change created, one it
gained as one the change lost. During an unfinished merge the working tree already holds what
the merge brings in, so HEAD is taken merged with each commit MERGE_HEAD names, as the commit
finishing the merge would be (`git merge-base <base> HEAD <merging>...`): measured from HEAD
alone, the anchors the base opened after HEAD left it counted as the change's. A base that
shares no history with HEAD leaves no point the change began from, and the check stops (exit
20); a HEAD that names no commit yet is refused, as every command measuring from HEAD refuses it
(exit 13). A shallow clone needs its history back to where the two part, unless either names the
other as a parent in its own commit - a pull request's merge commit checked out alone names the
tip it merges into - and where it may stop first, the check says so and asks for the rest (`git
fetch --unshallow`) rather than calling the two unrelated; a base it holds no commit for is said
the same way. The receipt names both commits where they differ, and the merge in progress. Each
registry is read at the commit compared with as `--current-commit` reads a file (see Citations),
so a file git cannot read is refused rather than taken for one that did not exist yet. A
registry absent there counts as empty, and the receipt says so. One malformed there is noted
too: a row it hid was not counted there, so deleting it goes unseen and repairing it reads as
new - noted rather than failed, since no change can repair the history. A registry git ignores
has no history and is refused.

Anchors are compared by id across both registries, so moving a row counts as nothing. The
check fails when open anchors rose, less the anchors newly disclosed and plus the closures that
are bookkeeping and the open anchors lost: when the change created more anchors than its work
closed. The first two corrections pull opposite ways for one reason. Disclosure records debt
that already existed, so writing it down is not creating it. A bookkeeping closure - a row
closed since the change began whose Trigger opens with the bookkeeping pair, `✅🧾`, where
`anchors.triggerCarriesVerdict` holds - records work that already existed, so marking it is not
doing it: the anchor leaves the open count, as a closed row must, and the change is credited
with nothing for it. The pair counts only where it opens the Trigger, as every mark counts only
where it opens its cell: one later in the prose could be claimed by a row that merely mentions
bookkeeping. A closure whose Trigger opens with the two marks where they are not read as the
pair is credited as work, as it reads, and noted. A closure is bookkeeping too, and noted, where
its anchor had a closed row already where the change began, beside its open one: the change
took away only the open copy of a duplicate.

The check also fails whenever the registries as they stand are unsound: a closed anchor in
pending, a live anchor in done, a Status that is none of the four spellings, a row stating two
verdicts where `anchors.triggerCarriesVerdict` holds, an id with more than one row, a missing
registry, or a structural problem. And it fails when an anchor that had a row where the change began, open or closed, is
in neither registry now: a row moves between them and is never deleted, so a lost row -
deleted, or renamed by hand - is listed apart. One open there is not credited; counted as
closed, it paid for an anchor the change created, which went unreported until the row came
back. One closed there changes no count, but the done registry is the record of the work done,
and no other check compares the registries with where the change began: `read-anchors --lint`
reads them as they stand, and `check-anchor-citations` notices only a citation the row leaves
behind. A row is judged lost only where both registries were read whole, since every row a
missing or malformed registry hides would read as lost; and one whose Anchor cell did not name
one id there is noted rather than failed, since repairing that cell changes the id its row
reads as. Every problem is reported at once.

### Citations

`check-anchor-citations` requires every anchor id cited in a **scanned root** to resolve to a
row in either registry. The roots are `anchors.citationRoots`, and nothing outside a declared
root is scanned: which code is production code is a judgement a repository makes, not one a
tool can infer. An empty list scans nothing and the command says so, rather than reporting a
pass over a check that looked at no file. `--current-commit` reads HEAD, `--current-tree` reads
the disk, `--current-pr` reads only what this branch changed, measured as the balance measures a
change, an unfinished merge included. A HEAD that names no commit is refused by the two that
read from it (exit 13).

A citation resolves to a row whose id is exactly the id cited, by the rule `read-anchor` finds
a row by, so the two verbs never disagree about whether a row exists. Resolved by containment
instead, a citation of `D-FF3-3` passed through a row `D-FF3-30-…`, and a wrapped fragment
`D-PP-PRESCAN-` passed through the id it was cut from: a truncated or ambiguous citation was
invisible to the gate. A citation that runs into a hyphen at the end of its line is reported as
cut there whatever rows exist - even one named by the part before the cut - because it does not
spell the id it was cut from. An id cut at its first or second hyphen is too short to be a
citation on its own line, so it is one where the next line carries on with the segments that make
it one: `D-PP-` before a line opening `PRESCAN` is reported cut, and `D-` before `day` - a wrapped
D-day - is not. An id cut just before a hyphen is cut too: one that ends its line where the next
opens - past its indentation and a comment's marker - with the hyphen and the segments that carry
it on into a row's id, as `D-LK6-14` before `-INTEGRATION-PAYLOAD` where
`D-LK6-14-INTEGRATION-PAYLOAD` is a row. Read on its own it resolved to the shorter row it happens
to spell, or to none. Where the two lines joined spell no row, the hyphen opens something else - an
option such as `-Wall`, a figure such as `(-40`, a line a diff removed - and read as a cut each
failed the check over an id written whole; a list's `- ` and an option's `--` carry nothing on
either way. A break inside a segment, with no hyphen on either side, is cut the same way where the
two lines joined spell a row's id, as `D-LK6-14-INTEGRA` before `TION-PAYLOAD`; where they spell
none it cannot be told from a line that simply ends there - a figure such as `MF-4` or the next id
of a list opens a line as often - so it reads as the shorter id, reported unresolved unless that
shorter id is a row of its own. The failure says cut citations apart from those no row resolves,
since adding a row answers only the second.

An id followed by `*` or `{`, or by a hyphen and one of those, names a family or a pattern of ids -
`D-AREA-TOPIC-*`, `D-AREA-TOPIC-{A,B}` - and no row, so it is no citation; an id in bold,
`**D-AREA-TOPIC**`, is one.

`--current-commit` reads every file of the commit through one git process; asked for one at a
time, each cost two, and 2,385 files took twenty minutes. A file is read as it would be from
disk - its byte order mark, UTF-16 included, says how. git answers "missing" for a file whose
object it cannot read exactly as for a path that names none, so a path it answers that way is
looked for in the commit's listing: one listed there refuses the read, naming it, rather than
passing with nothing read in it. `check-anchor-balance` reads the commit it compares with
through the same rule, so a registry git cannot read there is refused rather than reported
missing.

git holds a name as bytes, and nothing makes them UTF-8. A file in a root whose name is not
UTF-8 refuses the check, named as git's quoting writes it (`caf\351.md`): no file opens by such a
name here, and read as UTF-8 it became another name, which the disk silently did not have. Which
root a name lies in is decided on the name as .NET reads it, a stray byte as U+FFFD, and never on
the quoted form, whose backslash reads as a separator and moved `src\351.bak` into `src`. A file
git lists once for each side of a conflict is scanned once; two names that only read alike are
two files.

The scanner is the point of the command. The guard it replaces required a word boundary before
an id, which is right for `FIXED-32-BIT-WORD` — whose tail is anchor-shaped and is correctly
skipped — and wrong for an id written straight after an escape, as in the C++ literal
`<< "\nD-SOME-ID: …"`: the `n` of `\n` is a letter, the boundary fails, and the whole citation
is not reported missing but simply never seen. A wrapped id does not fail, it disappears. So a
preceding word character blocks a match unless it is the tail of a recognised escape sequence,
and a doubled backslash counts as a literal backslash, which is not one.

## Hosts, trees and legs

Three concepts that are frequently conflated, kept separate here:

| Concept | Question it answers | Values |
|---|---|---|
| **Host** | Where does this run, and how is it reached? | this machine, a WSL distribution, an ssh host |
| **Tree** | Which checkout am I acting on? | main checkout, a worktree, a host's copy, a leg's own copy in the tree's `.legs` |
| **Leg** | One unit of work with a verdict | `(os, processor, emulator?, project, toolchain, config, sanitizer?)` |

A worktree is **not** a host. It is a tree, and it composes with any host. Keeping
these apart is what lets one code path serve `build`, `build --worktree wt-a` and
`build --ssh vps` without special cases.

### Where a leg runs

A leg names what it needs, never where it runs: an operating system (`windows`, `linux`,
`macos`), a processor (`x86_64`, `arm64` and the other processors .NET reports), and
optionally the emulator it runs through on a host with another processor, which rules out
running it natively. A misspelt value is refused when the file is read, since it could
never match a host.

Where it runs is measured before anything starts, never declared:

- A leg's candidates are this machine, then the WSL distributions under `hosts.wsl` when the
  leg runs on Linux, since a distribution runs nothing else, then the ssh hosts under
  `hosts.ssh`, each in the order the configuration declares them and named as it declares
  them. A leg that sets `wsl` or `ssh` has that one host as its only candidate.
- This machine is measured first, because it costs nothing to reach. Other hosts are
  measured only for the legs it cannot take, and all at once, so an unreachable host costs
  its connect timeout once.
- A leg runs on the first candidate whose measured operating system matches and whose
  processor matches, or, for an emulated leg, where that emulator's check passed. A program is
  never part of the choice: a host that lacks one the command requires there turns the leg away
  there, rather than the leg moving to a host that has it and measuring a machine nobody chose. Every
  command places a leg the same way, so a run with `--use-staged` finds the tree where a sync
  put it. To run a leg elsewhere, the leg names that host.
- `--legs` takes leg names and leg set names, separated by commas or spaces. A name that is
  neither is a usage error before any host is measured. No `--legs` selects every leg, and
  `--legs` given without a name is a usage error rather than every leg, so an empty
  variable cannot pass a gate. A leg or leg set name holding a comma or a space could never
  be selected, so the file refuses one.
- A leg that cannot run is a warning naming the leg and why - each candidate's reason when none
  could take it, or what the host it went to lacks - and the other legs still go ahead. The
  check fails, and `legs` exits 1, when a leg named with `--legs` cannot run, or when no
  selected leg can: a declared leg on a machine that is switched off is normal, and a leg asked
  for by name is not. It exits 70, named or not, when whether a leg can run was never
  established - a host never asked about a program the leg starts, which is a defect in this
  tool.

A native run and an emulated run are different legs. Neither their timings nor their
failures compare.

A leg's work happens in a tree, and which tree is not where it runs: a leg a host runs works in
that host's copy of its tree, whatever anything else says. A leg this machine runs itself works
where the first of these says anything: `--local-copy` or `--in-place` on the command, for that
run of `build`, `test` or `run`; a runner's `writesTree`, which works in place; the leg's own
`localCopy`, `true` or `false`; the repository's `defaults.localCopy`; and, where none says, its
tree itself. A runner that writes the tree is refused before any host is measured where a host
would run its leg, and with `--local-copy`, since what it wrote in a copy the next sync would make
the tree again; a run check whose runner writes the tree runs within the leg's own work, and is
refused wherever the leg it checks would work in a copy (`LegTrees.InLocalCopy`, the rule the
placement places a leg by). The key is refused
when the configuration is read on a leg that names a host, whichever value it gives, since a
host's leg always works in that host's copy; the default never is, for it says nothing of such a
leg. Where
the leg works in a copy, the copy is kept in the tree's own `.legs` slot, one per tree and
variant and named by the variant's key, made from the run's one reading of the tree as a host's
copy is and kept warm between runs: its build directory is the variant's own within it, and a
write the tree receives while the leg runs - a fold, a commit, an edit - ends nothing of the
leg's, which ends on the tree its run read. A leg naming a worktree gets a copy of that worktree,
of its own. The first build in a copy starts from nothing, said on the leg's line, for a build
directory holds absolute paths of the tree it was configured in; files git ignores are not in a
copy, by the one rule that withholds them from every copy; and the room a leg needs is reckoned
for the tree its build is in, so a copy's first build is measured by what the variant came to in
the tree and in every other copy, leg copies included among the trees a host is asked about. Each
tree this machine holds is listed with its leg copies after it, named as its - `the main
checkout's leg copy`, `worktree o1/xa's leg copy` - so a compiler working in one is named as that
copy's and never as nobody's. A copy is a repository of its own, as every copy a sync makes is,
and holds the tree's HEAD and a `.git` of its own; the leg works by the configuration and the
actions the reading carried, while values, secrets and the run lock are read where this machine
keeps them, and its records stay in the run's own directory. Its starting line names where it
works - `starting on local, in its copy '<path>'`, or `, in '<tree>' itself` - and a leg in place
that ends `inputs-moved` names the remedy, the key, the default or the option that would have run
it on a copy, where the placement's own rule (`LegTrees.InLocalCopy`) could have put it in one: a
leg another machine sent here, or one typed in a copy a sync made, already works in a copy and is
named none. A copy whose build would pass this machine's path limit refuses the run before
anything starts, with the numbers and the way out; a tree whose `.legs` git does not ignore
refuses it naming `init`, as a sweep's workers do, before the run claims its directory of records,
so nothing is left for the next run to call abandoned. `check-mutations` makes no copy of its own
on this machine - its workers are already copies of one reading - and `clean` and `sync` place
nothing in one. On another machine a sweep works in a copy of its own (`LegWorkload.CopyOnHosts`),
kept in the `.legs` of that machine's copy of the tree and synced from this machine's reading as
that copy is (see "Locks, records and hosts").

### Emulators

An emulator runs programs built for another processor without leaving the host's
operating system: qemu's user mode on Linux, Rosetta on macOS, Prism on Windows. It
declares the hosts it runs on, the processor it runs programs for, the launcher placed in
front of each program (none where the operating system runs such programs itself), what it
requires, the phases it runs (tests by default, since the usual way to build for another
processor is a native cross-build), and a witness.

The witness is a program run through the emulator whose output must match a pattern, such
as `uname -m` from an arm64 userland printing `aarch64`. Without it, an emulator that ran
nothing, or a program the host quietly ran natively, would count as coverage of a processor
that was never exercised.

A whole virtual machine is not an emulator in this sense. It runs an operating system of
its own, and is declared as the ssh host it is. A virtual machine with a different processor
runs DssHarness under full emulation, where .NET is not supported, so DssHarness itself
is not dependable on such a host.

### Reaching a host

- **WSL.** A distribution is available when this machine runs Windows, `wsl.exe` exists,
  and a program starts in the distribution. Programs start with
  `wsl.exe --distribution <name> --cd ~ --exec`, so no shell in the distribution parses
  the arguments, and with `WSL_UTF8=1`, because wsl.exe otherwise writes its own messages in
  UTF-16. Its errors are recognised by the code they carry, such as
  `WSL_E_DISTRO_NOT_FOUND`, never by their sentence, which is translated. `--wsl` with no
  name is WSL's default distribution, as the distribution itself reports it.
- **ssh.** A host's connection data lives in its own directory under
  `.harness-config/sshItems/<name>/`, which git ignores: an `.env` naming the address, the
  user and the port, a `.key`, and a `known_hosts`. `config.json` declares only the directory
  names, under `sshItems`, and `hosts.ssh` is keyed by them. Nothing tracked names an address,
  a user, a key path or a credential, so a `config.json` that arrives through git cannot point
  the harness at a machine nobody set up here, and a public repository carries no connection
  data at all. There is no wildcard and no pattern matching: a directory either has the name or
  it does not, which is what a shared ssh config file made subtle. ssh is invoked with that
  item's key, port and known-hosts file, in batch mode, so an untrusted host key or a password
  prompt fails with ssh's own reason instead of waiting, and `connectTimeoutSeconds` and
  `keepAliveSeconds` bound a dead link. An `.env` other users can change is refused, because
  whoever can change it can send the harness somewhere else; a key other users can read, ssh
  ignores, so it is refused too; and an `.env` other users can read that holds a `SUDO_PASSWORD`
  is refused, as a credential already shared - one written blank holds none, declares none, and
  is no reason to refuse. Each is checked before connecting, and reported with the command that
  fixes it. Per-host files also confine that risk: one world-writable shared
  configuration file threatened every host at once.
- **Names that resolve.** A host reached by an mDNS `.local` name on a DHCP network fails a
  lookup as a matter of course, and a name each ssh call looks up afresh is one each call can fail
  to find. So ssh is asked first what it would do (`ssh -G`), and the name it would look up - the
  address declared, or a HostName its own configuration gives it - is looked up here, retried,
  with the answer cached briefly, so one failed lookup never fails a leg. Every ssh call is then
  given the address as its `HostName`, while the pin holds, with the host's key looked up under
  the name through `HostKeyAlias` - its configuration's own alias where it sets one, and
  otherwise `[name]:port` off port 22, as known_hosts spells such a host - and `CheckHostIP` off,
  so the address itself is neither checked against known_hosts nor written into it. The
  destination stays the address declared, so a Host block written for it still applies. A host ssh
  reaches through a `ProxyJump` or a `ProxyCommand` is neither looked up here nor pinned, since the
  jump host or the command does its own lookup. One whose `ssh -G`, asked again pinned, shows
  anything but the address changed, as a `Match` block keyed by the host would, is looked up here
  but not pinned. A pinned call that fails before any session - the address takes no connection,
  or shows a key the name is not known by - drops the pin for the rest of the connection and runs
  again, with ssh looking the name up itself. For a host whose name is looked up here, no reason,
  relayed line or document names an address the name resolved to, since it is not the reader's to
  publish: wherever ssh, or a program on the host, names one as a word - the pinned address timing out, a key refused as `user@<address>:
  Permission denied`, another of the name's addresses dialled once the pin is dropped, an address
  with its port after it in ssh's debug lines, one with a label stuck to its front as `ifconfig`
  prints it - it is written as the address declared. An IPv6 address is matched as the address it
  is, because ssh on Linux names a link-local one's scope by its interface (`%eth0` for the `%2` it
  was given), and an IPv4 one only as it is always spelt, so a version such as `192.0.522` is never
  taken for `192.0.2.10`. The addresses are shared by every copy of the connection, and the name is
  looked up again, afresh, before every call that lets ssh look it up itself - one never pinned,
  though not one through a `ProxyJump` or a `ProxyCommand`, which nothing here looks up, or once its
  pin is dropped, since a machine that slept can wake with another lease - so whichever address ssh
  dials is known before it is named, wherever this machine's own lookup returns it too. That holds for what the host's own programs print
  as well - its relayed lines on both streams, its leg's reason and last lines, and what it found
  about itself - so no two copies of a line differ in what they name.
- **Hosts that sleep.** A personal Mac reached by its mDNS name falls back asleep between commands
  and answers again moments later; three quick lookups miss it, and a consumer saw a run skip it
  seconds after a check had reached it, three times in fifteen minutes. A host given
  `wakeWaitSeconds` is looked up again, and a connection nothing took or that timed out is tried
  again, every few seconds until the window ends, before its legs are skipped. What a host that is
  awake says - a key it shows that the name is not known by, a login refused - is never waited on.
  A host reached after waiting says how long it took, among what measuring it did; one whose window
  ran out is refused naming the window, and, for the half minute a name's answer is kept, refused
  at once to the rest of the command rather than waited for by every leg placed there. Left at 0,
  the default, nothing changes.
- **PATH truth.** A login shell's PATH is not what a command sees: `/opt/homebrew/bin` is absent
  from an ssh command's PATH on macOS, and `~/.dotnet` is in WSL. Programs the harness depends on
  are resolved to an absolute path once per connection, measured rather than assumed, the same
  way the remote shell is. A host whose SDK is installed but off that PATH is reported as exactly
  that, never as "not installed": the remedy differs. A leg's own programs are found by the
  DssHarness on the host that runs it, with the function the leg's run uses: on that PATH, then
  in the searched directories (`toolSearchDirectories`, or a built-in list). The directory each
  was found in is appended to the PATH of every process the leg starts, so the run finds what
  the survey found even where a phase's environment sets a PATH of its own - which is why a
  program started under such an environment is looked for too, though it never turns a leg away.
  A directory the search could not look in leaves a program unknown, never missing.
- **A tool's minimum version holds every copy a leg could start.** A declared tool with a
  `minVersion` is found where its first copy that meets it is - on the PATH, then in the
  searched directories, each copy asked its version with the tool's own probe until one does -
  and where an older copy comes first, the directory of the one that meets it is raised ahead of
  the PATH of every process every leg on that host starts, every other program in it coming with
  it, as a shell Homebrew set up puts `/opt/homebrew/bin` first. That is the one exception to
  appending, and the survey resolves every other program with the raised directories ahead, so
  survey and run still agree. A host answers for all of its legs at once, so the directory is
  raised for the legs the tool is not declared for too, and ahead of a PATH a configuration or a
  developer environment sets: the copy chosen is the one started whichever PATH it goes ahead of.
  Three outcomes, kept apart as "not there" is kept from "could not look":
  - every copy said an older version: a leg the tool is declared for is `skipped-tool-missing`,
    naming each copy and what it said, and `dssharness install-missing-tools --legs <leg>` where
    the tool declares an install there;
  - the copy that meets it is behind an older one in a directory raised for another tool: the leg
    is `skipped-tool-missing`, naming both and what to update. The directories go up in the order
    that keeps every tool on a copy that meets its minimum - a directory only once each tool it
    holds an older copy of is started from one ahead of it, the directory of a tool's first copy
    raised too where that is what it takes - and where no order does, the tools in the way are
    asked about their further copies for one in a directory that can go up. A tool is shadowed
    only where nothing settles it, the directories then going up as the tools are declared; and
    nothing beyond what a leg starts is asked where nothing is in the way;
  - no copy was shown to meet it - one could not say a version this can compare, or a place a copy
    could be was not looked in: the leg is `skipped-unavailable`, and nothing names an install,
    which would settle nothing.

  A leg the tool is not declared for is turned away for none of them. A leg through an emulator
  is held so over the programs its emulator is checked with too - its requirements and the program
  its witness starts: the emulator counts a copy held to a minimum as there and starts its witness
  with it, since whether a minimum is a leg's is the tool's scope, which only the leg can say -
  wherever its command starts programs: a sync or a clean starts none through it, so turns no leg
  away over them. A leg in a developer
  environment holds the copy that environment's PATH names to the tool's minimum once it is set
  up, as it looks for its programs there. A survey request carries the minimums, which its host
  holds every copy it finds to, and its answer the directories they raised beside the appended ones
  (`appendedDirectories` on the wire, no longer every directory a leg's PATH is given), each
  program found with the copy judged against its minimum and the one passed over for it (`judged`,
  `passedOver`), and the looks in developer environments `install-missing-tools` asks for
  (`inDeveloperEnvironments`, both ways): the host-agent protocol's rise to 11. Measured on a Mac: the PATH of a command run over ssh
  named macOS's own python3, 3.9.6, and a leg whose configuration needs 3.12 ran its steps under it
  without a word, failing a self-test for a reason that named something else.
- **No quoting.** An ssh server hands its command line to a shell, and which shell is not
  known in advance: sh, bash, zsh, fish, tcsh, cmd or PowerShell. The harness quotes for none of
  them. The command line holds only words every one of them reads literally (letters,
  digits and `._-/=:+`), save the mark written ahead of it in the measured shell's own syntax
  (below), and anything else travels on standard input. Which shell answers is
  measured once per connection by `echo %COMSPEC% %COMSPEC%`: cmd expands it, a POSIX shell
  prints both words on one line, and PowerShell, whose echo writes each argument as an object of
  its own, on two. cmd and PowerShell read a backslash literally, which a Windows path needs, and
  are asked where a program is with `where.exe` - PowerShell reads a bare `where` as its own
  Where-Object, which finds nothing and says nothing. Neither can look in a searched directory, so
  the harness's own SDK off the PATH there is unknown rather than missing: the one program looked
  for through a host's shell, every other being found by the DssHarness that SDK runs there.
  PowerShell writes to standard error
  in a way of its own.
- **What a host's shell prints first is its own.** A shell an ssh server starts can run the
  account's startup files before the command - bash run by sshd reads `~/.bashrc` - and what they
  print arrives first, on the streams the command writes. So every command the harness sends over
  ssh is preceded by its shell writing a mark of the harness's own, random for each command, as a
  line on standard output and then on standard error, and each stream is read from after its first
  mark line, as it arrives and as it is kept alike: what came before is neither kept nor relayed.
  Every shell but cmd and PowerShell writes the two through `sh -c`: tcsh has no redirection of
  standard output alone to standard error (measured with tcsh 6.24, `1>&2` wrote the mark into a
  file named `2` and left standard error unmarked), and a shell tracing its commands under
  `set -x` writes the trace of an `echo` of the mark to standard error, ending as the mark line
  does, where the trace of `sh -c '...'` ends with its quote. A stream that never carries the mark - a
  connection that failed before any session - is all there is, and is kept whole, its lines
  relayed once it ends. Measured on one consumer's Mac: every command's error carried the lines
  emsdk's environment script prints, and an install's failure quoted them in place of the
  shell's own words. The probe that names the shell is the one command sent unmarked: how to write
  the mark is what it answers.

### DssHarness on every host

Every WSL distribution and ssh host runs DssHarness itself, installed as a global .NET tool
from nuget.org, so it needs the .NET 10 SDK, with `dotnet` on the PATH of a command run
without a login shell. DssHarness itself is started from `~/.dotnet/tools`, where global
tools are installed, since that directory is usually on no such PATH. Every install and
update names nuget.org as its only source, so no feed configured on the host can supply a
different package under the same name. Only stable versions are published there: a beta is
released on GitHub alone, so a machine running one cannot bring a host to its build, and
is told so. The machine that reaches it asks it questions
through a hidden `host-agent` command, with the request as one line of JSON on standard
input - written as it is encoded, so a request carrying files is never held whole as text on
the machine that sends it - which it holds open until the host has finished: which build
it is, what the host is, and whether each emulator works there; or to run one of its own
commands in the host's copy of the repository, which is what `host-exec` does.

Nothing a run request starts outlives the machine that asked for it (`HostAgentService`). That
machine has gone when the input it holds open ends, or can no longer be read, whatever the failure;
and, since the end of an input is only as good as whatever carries it - a consumer's leg ran on to
its end on a host reached over ssh, with nobody reading it, after its dispatcher was killed, where
the same leg in a WSL distribution was cancelled at once - when the beat it writes on that input
has stopped. Every request says how often its asker writes one (`HostAgentRequest.BeatSeconds`, 15,
since protocol 8; protocol 9 is the copy's history, below); the process runner writes it for as long as the input is held open
(`ProcessRequest.StandardInputBeat`, set from the request by `HostAgentProtocol.BeatOf` wherever a
run request is sent: a leg, `host-exec`, each of a sync's operations), and the host takes anything
read on its input as one. Eight in a row unheard (`BeatsMissed`), two minutes, and the host cancels
what the request started: it looks at that silence four times in the two minutes (`SilenceLooks`),
so the cancellation comes between two minutes and two and a half after the last beat heard, never
sooner. It first says why on its error stream - the one sign that can be wrong, so a machine still
there, whose beats were held up, reads why its run stopped - from another thread and for two
seconds at most (`HostAgentPatience.Saying`): the console's writer writes where it is called, and
one whose reader had stopped reading held the cancellation it came before for as long as the
connection stood, which is the very machine the beat is there to notice. A request saying a beat
no machine writes - less than none, or more than an hour (`LongestBeatSeconds`) - is refused
before anything runs. On Linux and macOS a hang-up of the agent's session cancels it the same way
(`HangUp`), rather than ending the agent where it stands with its children left running; Windows
ends a process whose console closes whatever it answers, so nothing is promised of it there.

What was cancelled is given a minute to stop (`HostAgentPatience.Unwind`), and a command past its
point of no return (`PointOfNoReturn`) two (`Finishing`), which is what the command line gives one
interrupted where it was typed: a deletion goes on once cancelled and stops git shortly before
that time is up to say what is left, and cut off at a minute it was ended part way with nothing
said. A command that has not stopped by then - one reading to its end the output of something its
child left running, which never ends - is ended by the agent's last resort
(`HostAgentLastResort`): every process the agent started, each with its tree, as the process table
lists them, then the agent itself, exit 130, whatever the ending of them came to. Which processes
those are is read from one table: the parent's start and each child's by the same clock - on Linux
one that counts from the machine's start, so that held against the agent's own start as the
runtime tells it every child read as older than its parent and none was ended - and never the
console the agent runs in, which Windows lists as its child (`conhost`) and ends everything in the
console with. The agent's last line says what that came to: each process ended, by its id, each it
could not end, or why none could be listed. The one thing started on a host to outlast the request
that asked for it is the process holding the host awake between commands (`holdAwakeSeconds`),
which ends on its own seconds. There is no verb that stops a run on its hosts, because stopping
the command that dispatched it is that verb, on every carriage.

The beat can fail at this end too: the process that carries the request - ssh, wsl.exe - stops
taking what is written to it and goes on. The process runner then waits two seconds for it to be
seen exiting, which says the rest itself, and otherwise writes nothing more and records why
(`ProcessResult.BeatLost`). Each sender of a run request - a leg, `host-exec`, a sync's operation -
warns with it (`HostProbes.BeatLost`), since the host hears no more of this machine and stops what
it was asked for a machine that has gone, and that machine is here, reading how its command ended.
Passed over, as a child ceasing to read its input always was, the run ended with nothing at this
end to say why.

This machine's own DssHarness has one door too (`update-tool`, `ToolUpdateService`). Measured on a
consumer's Windows host: a `dotnet tool update` typed by one session while another's commands ran
took the tool's shim away for ten seconds, failed and put it back, twice, and every command
anybody typed meanwhile found no tool (127). The update moves the installed version's directory
aside before it writes the new one, and Windows moves no directory a running program has loaded
files from; elsewhere the move succeeds and takes the files from under what is running. So
`update-tool` reads the process table and moves the installation - this user's global tools, or
the directory `--tool-path` names - up to the newest release nuget.org lists only while no other
process named `dssharness` runs on the machine: with one running it changes nothing and names
each by its process id and the command it was asked, never the rest of its line, which may hold
an input's value (exit 13); with `--wait` it looks again every five seconds, saying who it waits
for as it starts and every five minutes; and a wait asked from inside a running DssHarness, which
would never end, is refused - as is any wait where the table was read by names alone (a degraded
reading names no process's parent, so such a wait could not be told from one that ends), the
refusal then saying why the table was. Once the update has begun the command stops neither it nor
the reading of what it left: both run under no cancellation, and `update-tool` is among the
commands past a point of no return (`PointOfNoReturn`), which the command line waits two minutes
for after an interruption, because an update cut off part way leaves a machine with no tool and a
command that says nothing of it. A Ctrl+C typed at a terminal on Linux or macOS reaches dotnet
itself, as it reaches every process of the terminal's: what that leaves is read back and said
like any other failure of the update. It says the update done only once `dotnet tool list` shows that release, and a failure with
what dotnet said, each DssHarness that started while it ran, and what is listed since - or, where
that listing failed too, why in dotnet's words (exit 20; 21 where dotnet did not finish within its
ten minutes - an update begun and not finished, which running it again can finish, the one code
`ProgramUpdate.FailedExit` gives every program's update, Claude Code's by npm among them; dotnet
says a feed it did not reach in the machine's own language, with no code to match, so only its
bound tells it). Which release is the newest is asked of
nuget.org (`IPublishedToolVersions`), and an answer that does not come is said with its cause
(`PublishedAnswer.Untold`): the feed not reached, no answer in three seconds, a status that is no
success, a list or a leaf this build cannot read - put right in different places, and once said as
one. Nothing is changed then, unless the copy asking is itself a release newer than the installed
one, which the tool is moved to with a line saying so; a copy that is no release is never that, as
nuget.org holds none. Versions neither of which a release can be told newer than are said as that,
the feed never asked. The command asking is a DssHarness too: typed by its name on Windows it is
itself what stops the update, so there it changes nothing and says the command that does, with the
options it was typed with (a path holding a space in quotes), on every system - `dotnet tool exec
DssHarness --yes --source <nuget.org> -- update-tool`, the newest release run as the program
`dotnet` from the package cache, holding none of the installed files (measured: an update beside
such a copy went through in a second), with the update's own outcome as its exit code. Elsewhere
the installed tool replaces its own files while it runs, having first loaded every assembly beside
it (`IOwnProgram.LoadWhole`), so nothing it has left to do asks for a file that is gone. A
DssHarness is told by the name of its program, as a host's is, so a build from source running
beside the installed tool is counted: wrongly, and safely. On macOS that name is read from a
listing of its own (`ps -o comm=`), since the path of a program may hold a space and the listing
that carries the command line does not say where the program ends: named by the first word of its
line, a tool kept under a volume called `My Disk` was a process called `My`, and not counted. Two
updates of one place never change it together: the change and the reading of what it left hold a
machine-wide lock named for the directory the tool is kept in (`ProgramUpdate.TryHold`, a
`MachineWideMutex` held by a thread of its own, since the change is awaited and a mutex is let go
only by the thread that took it). Two copies run beside the installed tool are each the program
`dotnet`, so neither counts the other, and an update that finds the lock held changes nothing and
is refused (exit 13), with `--wait` or without: it read the machine before the other changed it,
and waiting would only make the change again over that reading. A lock whose holder was killed
holding it is taken by the next update, which reads what was left as it reads anything installed.
What is left open is the moment between the last look and the update, in which a command just
started can still stop it: the failure then names that command.

Both ends must be the same build, so before anything runs on a host:

- A host without DssHarness has this machine's version installed.
- A host that is behind is updated to this machine's version. It is never downgraded, and
  not updated while DssHarness is running there: an update replaces a running tool's
  files underneath it on Linux and macOS, and fails part way on Windows.
- A host that is ahead stops everything (exit 13) until this machine is updated, with the
  command that updates it - `update-tool`, run from beside the installed tool. Moving the host
  down would undo somebody else's update.
- The version and the SHA-256 of the tool's assembly are both compared. A build from source
  reports the same version as the published package, while the assembly installed from one
  package is the same bytes on every operating system.
- An install or update is said with whether the host then answered as this build - the
  DssHarness it now runs read back, not the installer's word - and what stopped the host
  answering after one is said as coming after it: `updated DssHarness 0.6.8 to 0.6.9, then the
  host could not be reached: ssh said Connection timed out during banner exchange`. Said alone,
  an update beside a warning for every leg on that host that DssHarness there did not answer left
  a consumer unable to tell which version the host ran.
- A host that takes the connection and never says it is an ssh server within the connect
  timeout - asleep behind whatever took the connection, or still waking - could not be reached,
  as one whose name did not resolve could not: ssh's "Connection timed out during banner
  exchange" means no session began, so nothing ran there, and a host's `wakeWaitSeconds` waits
  it out as a connection opens, as it waits out a name that did not resolve.

`host-exec` returns the exit code of the command it ran on the host, unchanged, and 15 when
nothing could run there: the host is unreachable, has no SDK, could not be brought to this
build, or has no copy of the repository.

- The exit code is the one the host reports in its last line, which carries a value only
  that request knows, never the transport's. ssh exits 255, and wsl.exe with codes of its
  own, when a connection fails, so a line that never arrives is 15 too: the command may not
  have run, or run only in part.
- Interrupting `host-exec` stops ssh or wsl.exe, which ends the host's input, and the host
  cancels the command instead of leaving it running there.
- A host's copy is created by `sync`, which also puts `.harness-config/config.json` there so
  the DssHarness running there can find the repository at all. Sync will not take over a checkout
  made by hand unless `--adopt` names that host, and reports what taking it over would cost
  either way.

### A host's home is `~`

A host answering another machine writes into that machine's output: its terminal, its
`--json`, and every transcript and file that keeps them. So the host writes its own home
directory as `~` in what it tells that machine, as in
`~/src/app.worktree-x/.harness-config/runs/<id>`. The path still works in bash, zsh and
PowerShell there, which all read a leading `~` as the home.

- **Every line of DssHarness's own**, whatever the command: a leg's reason, a lock or
  free-space message, a refusal, a failure line quoting git or the system, and under
  `--verbose` a defect's report.
- **The paths in its ledger**: `runDirectory` and `mutationRecord`, a leg's `detail`, `tree`,
  `records`, each of its `logs` and its `space`, an arm's and a control's `records`, the admission's
  record and room, what a timing pattern matched, and the instance a developer environment was set
  up from. The machine
  that asked names each leg's own `records` in its `logs of` lines; the host's `runDirectory`
  reaches it as `--json` alone. A survey's answer
  names where each program is, the mount of a filesystem, and every reason the same way. The
  directories the programs were found in stay whole, because the machine that asked hands them
  back for a hold there to look for its command in; so does each build directory it asked
  about, which it matches to its question by that text.
- **Only the home, and only as whole path segments.** `/home/al` is not the start of
  `/home/alice`, and `/home/alice` is not the home inside `/data/home/alice`. A space after it
  continues the name too, since `C:\Users\alice smith` is not `C:\Users\alice`. A home reached
  through a link is matched both ways, because git names a repository's paths with every link
  resolved. On a Windows host the home is matched with either separator and in any case, and
  the separator after it stays as written: `~\src\app`. PowerShell reads that; cmd.exe,
  OpenSSH's default shell there, reads no `~` at all.
- **A program's own lines keep the home as it printed it**: a phase's last lines, in `logTail`
  (with that phase's name in `tailPhase`) and beneath a ledger's table, the output a command relays, and what an emulator's witness
  printed. A command typed on the host itself names its paths in full, as a command typed on any
  machine does. Under `host-exec`, a document other than a ledger - a listing, `legs --json` -
  is written as the host prints it. An address the host's name resolved to is the one thing
  rewritten in all of them, to the address declared (see "Names that resolve").
- **A copy's path this machine builds is said as configured.** The sync line, a lock held on a
  host's copy and delete-worktree's lines name the copy from `repositoryPath` as the
  configuration writes it, so `"repositoryPath": "~/src/app"` keeps the account out of those
  too. `keptOutputs` is relative to the tree already.

### Installing what a host is missing

`install-missing-tools` runs over every declared leg, or those `--legs` names, on the host each
leg names with `wsl` or `ssh` and on this machine otherwise. Its logic lives in the core, so
other commands share it, and `init --install-tools` calls it. Plain `init` installs nothing and
says how: adopting the harness's files changes one tree, and an install changes machines.

- **`--dry-run` installs no tool.** It reaches and asks every host exactly as a run does - its
  survey included, where a tool is asked there, which puts DssHarness on a host as `legs` does and
  needs the host's .NET SDK, so a host a dry run finds with none has every tool unknown - and
  reports each tool it would
  install or update as `would install` or `would update`, with the
  command that would run, `sudo` and all. Nobody is asked for a password, and no host is asked
  whether one is needed: a dry run that stopped for a password would be the install it says it
  is not. It exits `1` while anything is missing, and `--json` carries `dryRun`.

- **The .NET SDK is the default tool on every remote leg.** A WSL distribution or ssh host that
  cannot run DssHarness has it installed, under the home directory, where no login-free PATH
  names it — which is why every command the harness runs there spells the resolved absolute path.
  The machine running the harness is assumed to have it already.
- **The package manager an install runs is started where the host's survey found it**, as every other
  program the harness runs on a host is: brew in `/opt/homebrew/bin`, which no login-free PATH on
  macOS names. Measured on a Mac over ssh: the survey found brew there and called it current, and
  the install then ran it by its name and failed with "command not found". One found nowhere is
  run as written, and the host says what became of it; one run through `sudo` is left to sudo,
  which looks its program up on a PATH of its own. A dry run names the command as the install
  would run it.
- **Everything under `tools` that carries an `install` is probed and installed or updated
  through it**, by `probe.args`, `probe.regex` and `minVersion`. An entry with no `install` is an
  allowlist entry: probed where it declares a probe, reported when missing, never installed. That
  is how a program shipping with the platform, or with the repository, is allowed to appear in a
  runner's steps.
- **A tool is judged by the host's own survey**, the one `legs` runs there, which a leg there is
  placed by: one judge for both, so a tool is installed exactly where a leg would be turned away
  over it, and never a second time where a leg there finds it. Before this, the command looked
  through the host's shell on its own, and could call a tool missing or behind that a leg there
  started. A `minVersion` is judged as a leg finds the tool: the copy found first is asked its
  version, and where it is older, every other copy the host has - each directory its login-free
  PATH names, then each searched directory - in that order, until one meets it: that copy is the
  one a leg starts (see PATH truth), so the tool is current, named with the copy it is started
  ahead of. Judged by the first copy alone, macOS's own python3 kept a tool behind however many
  newer copies were installed beside it. Where every copy is older, the tool is installed or
  updated, and the host is surveyed again; where the copy that meets it is behind an older one a
  directory raised for another tool holds, the tool is outdated, naming both, and nothing is
  installed, since an install would not change which copy a leg starts; where none met it and a
  copy's version could not be read, or a place a copy could be was not looked in, the tool is
  unknown and nothing is installed. A host whose survey could not run has every tool unknown,
  saying why. A `minVersion` is written `major.minor.patch` - the parser every
  copy's version is compared with - and needs a `probe`; either is refused where it is written
  otherwise. brew installs and updates with `brew install`, which Homebrew documents as upgrading a
  formula that is installed and outdated: `brew upgrade` touches only what brew installed, never a
  system copy that fell below.
- **A tool may name the platforms it is needed on**, with `platforms`, in the same words a
  toolchain uses: `windows`, `linux`, `macos`, or `all`. A host whose platform an entry does not
  name is never asked about it, so it is neither probed there nor counted against that host's legs.
  Left out, a tool is needed everywhere, which is what every list written before this meant.
  Without it a repository could not declare both a Windows compiler and a POSIX one: each was
  reported missing on the other's hosts, and no leg was ever fully provisioned.
- **A tool may narrow that to the legs that need it**, and every scope it names must hold:
  `toolchains` (legs whose variant builds with one), `legs` (legs or leg sets, by name),
  `processors` and `emulators`. `processors` is the leg's processor, the one it is built for, which
  under an emulator is not the host's - so it reaches a native arm64 host as surely as an emulated
  leg, and what only the emulating host needs, such as the emulator itself, is scoped with
  `emulators` instead. Two legs on one host share what is installed there, and each is told only
  about the tools it needs: `cl` scoped to Windows alone was reported missing on a MinGW leg,
  because that leg is Windows too. A host is asked about a tool once, whichever of its legs need it,
  and not at all when none of them do. A scope naming nothing declared, or one covering no declared
  leg, is refused when the configuration is read.
- **Each leg is told about a tool as it will find it.** A leg whose toolchain names a developer
  environment starts every process in it, so a tool it needs is looked for on the `PATH` that
  environment sets up for its processor, as the run looks before the leg starts: set up on the
  leg's host for the look, by the DssHarness there, searched, then each directory searched for
  programs. Found there it is
  started by its path for its version, and `cl` - which no plain shell has - is never reported
  missing on the leg that builds with it. Every other leg looks on its host's own `PATH`, which is
  looked at once and shared, so two legs on one host can be told different things about one tool:
  CMake that only Visual Studio carries is there for the one and missing for the other, and a
  Visual Studio copy older than `minVersion` that comes first on its `PATH` is outdated for the leg
  that starts it, whatever the host's own `PATH` holds.
  - **An install runs once on a host**, for whichever of its `PATH`s asked first - the host's own
    comes first - and each then looks again, the environment set up afresh, since the install may
    have added to what it sets up. A failed install is the answer every leg there that needed it is
    given.
  - **An environment with no instance leaves the host's own `PATH` all there is**, so a tool
    missing there is missing, and an install that brings Visual Studio is what would help. One that
    could not be looked at or set up leaves a tool unknown, saying why, and nothing is installed:
    a second copy of a tool the leg finds is exactly what this command must never install.
  - **Every host's is set up by the DssHarness there**, the one that runs its legs, asked with the
    survey: a look names the environment, the legs' processor and the tools, carries what the host
    declares under `env`, which the environment is set up over as a leg's is, and comes back with
    each tool as the PATH it set up holds it, held to its minimum. Before this, another host's
    environment was out of this command's reach, and a tool its own `PATH` lacked was unknown for a
    leg in one there.
- **A privileged install takes its credential from that host's own item, on standard input
  only.** The item declares it as `SUDO_PASSWORD` in its `.env`, beside the address and user:
  `.harness-config/sshItems/<name>/.env`, or `.harness-config/wslDistros/<distro>/.env`. It never
  reaches an argument list, a log or an error message, and redaction happens at one place rather
  than at each call site: a failure excerpt was measured carrying one through. Only the six system
  package managers ask for one at all — `apt`, `apt-get`, `dnf`, `yum`, `apk`, `zypper` and
  `pacman` install into system directories; `brew`, `winget`, `choco`, `scoop`, `npm`, `pip` and
  `dotnet` never run under `sudo`.
- **A host that declares no password is asked for one, once, at the terminal.** Where nothing
  else can supply it, `install-missing-tools` and `init` ask whoever is running them, echoing
  nothing. The answer is checked against that host with `sudo -S -v` before an install that may
  run for half an hour rides on it, and a wrong one is refused on the spot rather than tried
  again: a second guess is a second failed authentication counted against that account.
  - **It is held per host, in memory, for that one command and no longer.** Each host owns its
    own answer, so a password typed for one is never offered to another — which would spend
    somebody's failed-login budget on a machine they never meant to touch. Nothing is written
    anywhere: the next command asks again.
  - **This machine can be asked too**, which is the one case an item could never cover: local
    legs have no item and so can declare no credential at all.
- **Nobody is asked where nobody is there.** A run whose standard input is not a terminal, and a
  run answering with `--json`, both refuse exactly as a run with no credential always has, so
  what a script reads keeps parsing. `--no-prompt` refuses the same way at a real terminal, and
  says so in the refusal. The refusal names every remedy, including running the harness as root,
  which needs no password: CI normally installs its dependencies in an earlier step and never
  reaches this at all, and where it does, running as root is the answer that suits it.
- **Interrupting the prompt stops the run.** Under `install-missing-tools` that is exit `130`,
  as any interruption is. Under `init` it is also `130`, and everything already created is still
  listed: the repository is initialised either way, and only the last step was stopped.
- A second run reports "already current" and changes nothing. An unreachable host is named, and
  the other legs still go ahead.

### What legs and host-exec run

`legs` and `host-exec` run what the configuration declares, without asking first: `legs`
runs the witness of each emulator the selected legs use, on the hosts it measures, and both
run DssHarness itself on WSL distributions and ssh hosts, which they install or update
there. That is the trust building the repository already asks for, since a build runs the
repository's own code.

- An ssh host is reached only when the main checkout holds its directory under
  `.harness-config/sshItems/`, which git ignores, and no ssh configuration file is named with
  `-F`: ssh reads the user's and the system's own, as it does for anybody. A `config.json` that
  arrives through git cannot point the harness at a machine nobody set up here.
- A launcher and a required file are each a program name, found the way a leg's programs are -
  on the host's `PATH`, then in the searched directories - or an absolute path. A relative path would resolve against whichever directory a host
  starts programs in, and would let a file shipped in the repository stand in for the tool
  it is named after. A witness with no launcher is named the same way. Behind a launcher it
  is an absolute path: the launcher finds it, not the `PATH`, and qemu's user mode opens a
  bare name in the directory it starts in.

### Verdict vocabulary (closed)

Every declared leg reaches **exactly one** verdict. A leg that reaches none is a
defect in the harness itself and fails the run, rather than silently vanishing
from the report.

| Verdict | Meaning | Counts as failure |
|---|---|---|
| `passed` | Ran to completion, succeeded, and its success pattern matched | no |
| `failed` | Ran to completion and reported failure; or a mutation arm's build failed at a step that is no object depending on its site - a link, another object - or named no step that failed | **yes** |
| `unwitnessed` | Exited 0, but its success pattern never matched; or a mutation arm's build, or its paired control's, passed with an object that depends on a site not rebuilt | **yes** |
| `inputs-moved` | Files the tests read changed while they ran; or, for a leg on a copy of its tree - a host's, or its own in the tree's `.legs` - a file was written, appeared or went while the tree was read, a file to carry changed after it was read, another command synced the copy between this run's sync and the leg taking it, or under `--use-staged` the copy is part made, absent or not this tool's; and nothing of the leg ran | **yes** |
| `unmeasured` | Whether those files held still could not be established - where they could not be watched at all, with none of the leg's work run; or what a phase printed could not be read back from its log where a verdict was to be read from it; or ninja's log could not be read around a mutation arm's build, so nothing witnessed what it rebuilt; or the report a mutation arm's run wrote, or its binary's unmutated run, could not be read from its file | **yes** |
| `contended` | Another process used the leg's build directory while it ran | **yes** |
| `skipped-not-selected` | Filtered out by `--legs`; or a mutation arm `--arms`, or its S row, leaves out of the leg, or `--pending` leaves out because its `passed` holds, and a leg whose sweep drives no arm | no |
| `skipped-unavailable` | No host can take the leg; its host or its tree could not be reached; whether a program it starts is there, or whether any copy of a declared tool meets its `minVersion`, could not be established; git could not answer in its tree; or no worker of its sweep fits the room left or the path limit, and every arm of the leg with it | warning |
| `skipped-tool-missing` | A required tool is not installed, or every copy of it is older than its `minVersion`, or the one that meets it is behind an older one a directory raised for another tool holds | warning |
| `refused-locked` | Another run holds the lock for this leg, or its host's tree | **yes** |
| `not-admitted` | A heavy leg waited its machine's `maxWaitMinutes` for a heavy-leg slot, for the memory in use to fall below the limit, or for room for its build beside what the other admitted legs claim, and nothing of it ran; or a unit of a sweep - a worker, an arm - waited so, and each arm the sweep had left once its machine refused one | **yes** |
| `log-held` | Another live run owns this leg's log path | **yes** |
| `poisoned` | The harness could not produce a verdict; one an exception ended names it, and how much memory the harness held as it gave the leg up. A mutation arm whose site could not be put back as it was, or whose driving ended in a failure nobody named | **yes** |
| `stopped` | Its work was begun or due and was stopped before it reached a verdict of its own: something stopped its build from outside before it finished - ninja, which says why whenever it ends a build itself, said nothing of why, or said it was interrupted; read only where ninja ran the build - or a filesystem a heavy leg's build fills had less free than its machine's `minFreeGiB`; or a mutation arm was not driven to a verdict: its sweep stopped, or ended by a refusal of the run, while it was driven or before; its own build, or its paired control's, stopped from outside; no worker left to run it; or the unmutated run of its test binary not passing | no: incomplete |
| `violated` | A mutation arm's declaration did not hold: a site or a cited text is not there, or a site is spelt otherwise than the tree spells it or is no file the sweep's reading of the tree holds; its before-text is not in its site exactly once, or is replaced by itself; its target or its runner is not built, or no object they build depends on a site; its mutation reddened other cases than its C rows, ran another number of cases, left a G row's case unrun or left out its diagnostic; a mutation declared to redden a test does not compile; or one declared to stop the build built, or its paired control did not | **yes** |
| `survived` | A mutation arm's mutation built and ran, and no case reddened | **yes** |
| `unattributed` | A mutation arm's run failed, and nothing ties the failure to a case: no report, one that is no JUnit report, a failing exit whose report names no failing case, or a run stopped for passing its bound, or as hung for printing nothing for `defaults.stallSeconds` | **yes** |

`failed` and `poisoned` are deliberately distinct: "your code is broken" and
"the harness broke" call for different responses. `inputs-moved`, `unmeasured`,
`contended` and `not-admitted` say nothing about the code at all: the first two call for
letting the tree settle and running again, the third for waiting for the other run, and
the fourth for waiting for the machine's other heavy legs, or freeing its memory or its disk.

`failed` and `stopped` are deliberately distinct too. A CMake build under ninja that exits
non-zero is `failed` where ninja said why - a step that failed, an error of its own, anything it
says under its own name but a warning or the directory it works in - and `stopped` where it said
nothing, or that it was interrupted: ninja says why whenever it ends a build itself, and one
killed part way exits 1, as a failed build does, and says nothing more (measured on Windows, with
`taskkill /F`). samurai, which CMake runs for a ninja generator where it is what is installed,
names no failed step as ninja does, and says everything under the name it was started by - the
file CMake's cache records - so that name is read as ninja's is. Running a failed build again
repeats the failure; running a stopped one again finishes it. A build the harness itself stopped
for hanging is `failed`, saying it hung, and so is a build under another build tool either way,
since only ninja's lines are read for this.

`refused-locked` and `log-held` are deliberately distinct, though both mean another run got
there first. A lock is taken for the duration of the work and is released by the run that
took it; a log path is owned by a run id, and one already owning it means two runs would
write one file and each would read the other's output as its own. The remedies differ -
waiting for the lock, against finding out which run still holds a finished run's logs - and a
reader who cannot tell which fired cannot pick either.

`violated`, `survived` and `unattributed` are reached only by an arm of `check-mutations`
(see *Mutation testing*), and a leg's verdict there is the worst of its own and its arms'. They
are three findings with three remedies: a declaration that does not hold is fixed in the
registry or in the code it guards; a mutation no test noticed calls for a stronger test; and a
failure nothing ties to a case calls for containing the crash or hang, or for a runner that
writes its report.

When several apply, the more fundamental one is reported: `poisoned`, then
`unmeasured`, `inputs-moved`, `contended`, `log-held`, `refused-locked`, `not-admitted`,
`failed`, `violated`, `survived`, `unattributed` and `unwitnessed`, with `stopped` before the
skips, whose legs' work never began, and before `passed`, since it reached no verdict of its own.
A finding about the code outranks the absence of evidence, so the three a mutation arm reaches
come before `unwitnessed`, as `failed` does. A leg whose inputs moved is not reported as failed
even if its tests failed, because what failed was a tree that never existed.

**A leg that reached no verdict is never counted among the legs that passed.** A skip is not a
failure — a switched-off machine is normal — and nor is a stopped build, but neither is a pass,
and a run carrying one exits `21` (`Incomplete`) rather than `0`, naming the legs that did not
report: those that did no work, and those stopped before finishing. So does a run where nothing
failed whose leg left a set-up it brought up running (see Set-ups). The verdict table
already ranks a skip above a pass so that such a run summarises as the warning; the summary now
reads that ranking instead of reporting the number of rows in the ledger as the number that
passed. A gate comparing two runs reads exactly this line, and "8 leg(s) passed" for eight legs
that never ran is the one number that must never be wrong.

## Parallel execution

A command that selects several legs starts them together and waits for **every**
one to finish before it reports. Legs are isolated from one another (see below), so
running them one at a time is never needed for correctness, and doing so would only
make a gate slower.

**Legs are chunked by the physical machine they run on.** A local leg and every WSL leg are one
machine, because a distribution runs on the machine running the harness; each ssh host is its own.
Two ssh names that happen to reach one machine are counted as two, because nothing here can tell
that they do. `defaults.maxParallelLegs` caps how many run at once **on any one machine**, and
`defaults.maxParallelLegsTotal` caps the whole fleet on top of that, for what a fleet shares even
when its machines do not: a license server, a network share, a sync's bandwidth. A single cap
across every leg had to be set low enough for the busiest machine, which left every other host
idle. A leg that says nothing about where it runs is counted as sharing one machine with every
other such leg — both answers are guesses, and that one only ever runs fewer at a time than the
truth would allow, while the other would remove the cap silently. Left unset, every selected leg
starts immediately.

**A parallel run is reported live, and every line says whose it is.** The run announces what it is
starting and across how many machines; each leg announces the steps it is about to run, then each
step as it starts and finishes; and a child's own output under `--verbose` is tagged
`<leg>/<phase>:`. Output is written a whole line at a time under one lock, so lines from legs
running at once never interleave within a line. The log files keep each line as the child wrote it:
the tag is for the terminal, so nothing that reads a log has to know about it. Only a line longer
than 32,768 characters is kept otherwise, in pieces of at most that many, each a line of its own,
since no line is held whole on its way there (see Reporting).

Within a leg the order is fixed:

```
sync (when the host needs it)  →  lock  →  admission (a heavy leg, where declared)  →  build on buildCores  →  test on testCores
```

- **A tree is synced once, not once per leg.** Legs on the same host share one
  tree; if each synced it, their copies would race over the same files. The legs
  sharing a tree wait for its single sync, then build and test in parallel, which is
  safe because each variant has its own build directory.
- **Cores are configured, never "all of them".** `defaults.buildCores` and
  `defaults.testCores` both default to 6. A host replaces them with its own
  `buildCores` and `testCores`, because a remote host rarely has the same core count
  as the machine that wrote the configuration, and a test invocation can replace the
  test count again with its own `cores`.
- **A host's `env` reaches every process a leg starts there** - each build phase, the
  ninja that reads the build's dependency records, the test runner and each step of a
  runner - as the lowest layer, so everything more specific still says otherwise. From
  lowest to highest: for a build, the host's `env` then the variant's (its toolchain's,
  build config's, sanitizer's and project's); for a test, the host's then the test invocation's; for
  a runner, the host's, then the runner's values and secrets, its own `env`, the action's
  inputs as `INPUT_<NAME>` where a `harness/read-inputs` step read them, and the phase's or
  step's. Names compare ignoring case on every platform, as they do on Windows: a value reaches
  every spelling of its name the machine already has, and the one written, so `Path` written for
  a Linux host sets its `PATH`, and `http_proxy` reaches both the curl that reads it and the
  tools that read `HTTP_PROXY`.
- **A PATH a host sets is the run's.** It is where that host finds every program a leg
  starts there, and no survey can see it, so none of those programs is required of the
  host before the leg starts - each is looked for, so the directory it is found in still
  reaches that PATH, and a program that is then not found fails the leg, naming it. A leg in a
  developer environment is the exception: the PATH it sets up is built over the host's, and
  what the leg starts is looked for on it before anything of the leg starts, a program missing
  there skipping the leg as a tool missing.
- **A host running a leg another machine dispatched to it is told which host it is.** To
  itself it is `local`, and `hosts.local` in the configuration the two share describes the
  machine that dispatched it: read that way, a leg on a Mac ran with the Windows machine's
  core counts and environment. The dispatch names the host, and its cores, its `env`, and
  the `{host}` a label records are all read under that name.

### Heavy legs share a machine

`maxParallelLegs` is counted by one command. Separate commands - worktrees each running a gate of
their own - share a machine that no one of them can see the others on: four such builds drove one
Windows machine's committed memory to 81 of 113.7 GiB, and the process that had started them died.
Where a machine declares **admission** - `defaults.admission`, or its own section under
`hosts.local` or an ssh host, whose fields replace the defaults' one by one - each heavy leg, its
tree synced and its lock taken, waits for the machine to take it:

1. **One of the machine's `heavyLegs` slots** (2, at least 1), shared by every command this user
   runs there, given in the order legs asked for one. The slots are kept in
   `<user data>/dssharness/admission-<machine id>.json` - the user data being `LOCALAPPDATA` on
   Windows, `~/Library/Application Support` on macOS, and `$XDG_DATA_HOME` or `~/.local/share` on
   Linux - beside the hold that keeps the machine awake: one per user of the machine, whichever
   repository asks, never in a directory other users can write, and never anywhere else when no
   such directory can be named. The record is named by the identifier the system keeps for the
   machine (Windows's `MachineGuid`, Linux's `machine-id`, the Mac's hardware UUID), never by its
   name, which a Mac takes from each network it joins: read by name, the legs still running under
   the old one would be another machine's to every command started under the new, and their slots
   free. Where the system keeps none - a container its image gave no `machine-id` - the record is
   named by the machine's name, and the first heavy leg says so and why. One record per machine
   also keeps a home two machines share from losing entries, since the lock a change is made under
   holds on one machine.
2. **Then the memory in use below `maxMemoryPercent`** (76, above 0 and at most 100). Where another
   leg holds a slot, a reading below the limit is read again after a settle - a time picked at
   random within `settleSeconds` ([15, 90]; `[0, 0]` reads once) - and the leg starts only if it
   still is, so two legs taking their slots together do not both start on one reading. Looked at
   again every `pollSeconds` (30, at least 1); the slot is looked at again too, so a leg whose place
   went while it waited waits its turn again. Seconds are at most 3600 and the wait at most 10080
   minutes, so every wait is one a timer holds.
3. **Then room for its build**, where its need is known: on the filesystem the build fills, what is
   free less what every other admitted leg there claims must hold it. Claimed as the leg is let
   start, under the room record's lock, so two legs never both take the one room left, and held with
   its slot. A leg waiting for room keeps its slot, so the legs behind it wait too; see *Room is
   claimed as a leg is admitted* below.

What a command's own configuration declares is the rule its legs are admitted by: a repository that
declares none never joins the line. Each entry of the record carries the count its command allows,
since commands of repositories that declare different counts share one record, and a leg starts
only while it is fewer legs from the front than the fewest any leg up to it allows: the legs holding
slots are always the front of the line, no leg runs beside more legs than its own configuration
allows, and none passes a leg that asked before it.

A waiting leg says where it stands as it starts each wait - who holds the slots, the memory in use, or
the room and who claims it - again whenever the holders or the claimants change, and again, as it reads
then and with how long it has waited of `maxWaitMinutes`, at least every 5 minutes: a consumer's leg
waited 39 minutes for the memory with one line, which its reader's pipe - passing each line on only
once the next came - delivered with the leg's admission, so the wait read as a hang. No wait
before a look runs past those five minutes, though `pollSeconds` and the settle may each be an
hour: a poll is cut at when the next line is due, and a settle is waited whole - the memory is read
again only once all of it has passed - in pieces of at most five minutes, the leg saying between
them that it still waits to look.

**A sweep is admitted unit by unit.** A `check-mutations` leg can run for hours, and held whole it
would keep a slot through every arm. So no slot is held for the leg: each worker is admitted as it
is made, claiming the room its copy and build still need, and each arm as it starts, each unit
holding a slot only while it runs and named `<leg>/<unit>` among the slots' holders. Only the
sweep's first unit settles: settling every arm would add up to a minute and a half to each. A
sweep in a WSL distribution is taken whole by this machine, which sends it there, as any heavy WSL
leg is, and runs one worker.

A leg holds its slot until its work ends - a runner's steps after its build, and a WSL leg's whole
run there, included - and keeps its lock while it waits, so another run of its variant is
`refused-locked` meanwhile, as it would be while the leg ran. A waiting leg counts against its
command's `maxParallelLegs` and `maxParallelLegsTotal` as a running one does, and each leg waits up to
`maxWaitMinutes` of its own - not counting a wait with only legs of its own run ahead of it, holding
slots or in line first, which is certain to end, which its line says is its own, and which a refusal
for a wait after it names as not counted. Another command's leg in line ahead makes the wait count,
whoever holds the slots: that leg takes the next slot given back, for as long as its own command keeps
it. Measured: a command's WSL leg, asked for at once with its two Windows legs by the process that
dispatched them all, waited out its hour behind them and was `not-admitted`. A host serving a leg for
another machine records the leg's slot under that machine's run, which the run request carries beside
the command (`HostAgentRequest.RunId`, since protocol 7), so the legs one command sends there wait for each
other the same way; a request naming its run by what is no run id, or naming a blank drive where WSL
keeps the disk, is refused, and nothing it asks for runs. A slot is held by the process that asked
for it, never by a timeout: given back when the work ends, and, where that process ended
first - a command that crashed or was killed holding a slot - reclaimed by the next leg that looks,
and said to be. Every entry of the record is this machine's, whatever name it carries, so an entry
is told by its process alone, where the run lock keeps one naming another machine until
`--force-lock` takes it. A slot this process could not give back - a full disk - is no longer counted
by its own later legs, and leaves the record with the next change it makes. A record that cannot be
read, one this process may not look at or one missing a member it needs included, is refused, naming
it, and never read as free: that is the one reading that would start every waiting leg at once.

**Heavy** is what builds or tests: a `build` or `test` leg, and a `run` leg whose runner - or a
runner its expected exceptions' run checks name, which run within its legs - builds, requiring the
build or running a step or phase that names `{product}`, `{buildDir}` or one of the leg's compilers,
or says `"heavy": true`, or runs a step whose action says `heavy: true`. A step's `heavy: true` holds whichever runner starts it,
one saying `"heavy": false` included: when weight was declared only on runners, a manual step that
rebuilds, kept in an action a light runner also runs, was started through that runner with
`--manual-step` and built with no slot at all. A heavy step limited by `runOn` makes heavy the legs
of those systems alone, and a step that performs a predefined action runs no program and cannot say
it. A run check's runner whose action cannot be read counts as heavy, said as the run begins, builds
nothing - not even where it declares `requireBuild` - and is refused by the check that runs it, as
before. A runner that only reads the tree - a repository guard - is light and starts at once. A
runner saying `"heavy": false` while it requires the build, or while a phase of its own names
`{product}`, `{buildDir}` or a compiler, is refused, naming all of them at once: its build is heavy.

**Room is claimed as a leg is admitted.** Where its build's need is known - its `buildSpaceGiB`, or
what a build recorded - and is more than its build directory already holds, a heavy leg is let start
only where that need fits on the filesystem its build fills beside what every other admitted leg
there claims, and holds its claim until its work ends. A command counts the room its own legs need as
it places them, but cannot see another's: two commands each placing one leg on one host both found it
room and filled its disk at build step 931 of 1295. The claims are counted whole, though a build may
have written some of its own already, which the room read now shows gone, and held until each leg's
work ends, its tests included: a leg may wait for that, and be `not-admitted` where it lasts longer
than `maxWaitMinutes`, rather than start into a disk it fills. A room never read in a leg's wait lets
it start, claimed against every filesystem of the machine, since nothing says which one it fills, and
its line says why; one read before and not now decides nothing, as a memory reading lost does not. A
WSL distribution's leg claims this machine's drive where WSL keeps its disk, where that drive was
measured as it was placed; the room inside the distribution's own disk was counted as it was placed.
An ssh host places and admits its own legs, a worktree's copy measured against every other copy there,
the main checkout's and each worktree's beside it. Kept in a record of their own,
`admission-<machine id>.room.json`, beside the slots': a build from before it, finding a member it
does not know in the slots' record, would refuse that record.

**A build is held to a floor.** While a heavy leg builds, each filesystem its build fills is read
again every 15 seconds, and the build is stopped once one has less free than its machine's
`minFreeGiB` (2 where the section says nothing, from 0 to 1024; 0 stops none): `stopped`, exit 21,
naming what was free and where, and what it built left for `clean`, as any build stopped part way
leaves it. A build that fails between two readings - a disk filled faster than the next came - is
read again as it fails, and is `stopped`, not `failed`, where a filesystem it fills is under the
floor then: a full disk says nothing of its code. So is a build whose own write fails then - a
phase's log, its record - which would otherwise end every leg of its run. The floor is read before
the build writes anything: one under it as it starts is stopped writing nothing, not its record,
which would say its directory was built from a tree no phase of it read; and a record of what it
built that it has no room left for is said, the one it wrote as it began standing. The room a leg
claims is only what is said of its build, and a consumer's leg whose need nothing said filled a 47
GiB disk to 79 MiB, under two other legs, before it died; the floor holds every build of a heavy
leg - a sweep's workers' too - its need said or not, and only where its machine declares admission.
A WSL distribution's leg holds the drive its disk grows on as well, which the distribution's own room
does not show: the machine that sends the leg names that drive beside its command, as it measured
it, and the distribution reaches it through its mount there (`/mnt/c`, as `/proc/mounts` lists it -
by the drive, or by the `path=` its options name, where an older WSL lists what it mounts as
`drvfs`); where it names none, or the drive is mounted nowhere, the build says only the
distribution's own room is held. A room that cannot be read stops nothing, and is said once, as a
leg is admitted without a room it could not read.

**The machine is the physical one.** A WSL distribution runs on this machine, so this machine's
command takes its heavy legs - by `hosts.local`'s rule, against this machine's slots and memory -
before it sends them there, and the distribution takes nothing again; a section under `hosts.wsl`
is refused. A command typed inside a distribution, rather than sent there, is the distribution's
own: it keeps a record there and reads the distribution's memory, and cannot see the legs commands
typed on Windows run, nor they its. An ssh host is a machine of its own: the DssHarness there takes
the legs sent to it by its own section, and the line it answers with names how.

**The memory in use is each system's own count** of what it can no longer give, as a share of what
it could: on Windows the commit charge against the commit limit, read with `GetPerformanceInfo` for
the whole machine - `GlobalMemoryStatusEx` gives the same figures only as far as a job object the
asking process runs in allows - and which grows with the page file; on Linux `MemTotal` less
`MemAvailable`, never `Committed_AS` against `CommitLimit`, which under the default overcommit stands
above 100% on a healthy machine; on macOS 100 less the free share in `kern.memorystatus_level`,
which `memory_pressure` reports as free. A WSL leg is admitted by this machine's commit, which
cannot see a distribution that has reached the cap its own configuration sets. A machine whose
count could not be read in a leg's wait takes the leg without it - on its slot, and its room where
its build's need is known - and its line says so, as a leg placed where its room could not be measured
is; one that stops giving a reading it gave is read again, never taken on the reading it last gave. A leg is let start only at a reading its own line
shows below the limit.

**WSL's page cache is given back before a leg waits on it.** WSL's virtual machine keeps what every
distribution's builds read and wrote as page cache, and gives it back only once it has idled for
minutes, which a machine running legs there never does; this machine's commit counts all of it. A
consumer's machine stood above its limit with no leg running, its virtual machine holding about 13
GiB of clean cache, and every heavy leg there waited its hour out. So on Windows a leg about to wait
on the memory has that cache dropped first - `sync; echo 1 > /proc/sys/vm/drop_caches`, as WSL's own
root, which asks no password, in the first running distribution a WSL host of its repository reaches
(every distribution runs in the one virtual machine) - at most once a minute, whichever leg of the
process asks, and reads the memory again once what was dropped has come back - a minute later at
the most, in place of its poll, and sooner where its wait ends or its next line is due first:
measured, a drop of 21.8 GiB of the 23.1 GiB cached took 3 seconds, and the commit fell from 93.8 GiB
to 73.9 GiB in two waves, the last done 49 seconds after. Its line says how much was dropped and the
memory before and after, and the leg goes on from that reading as from any other: below the limit,
it still settles where another leg holds a slot, and waits for room its build's need does not find.
A WSL leg sent from a machine that declares admission drops it as the leg ends, its line saying so,
without waiting: what comes back is the next wait's to read, and no command waits a minute for it.
Where WSL lists none of the distributions its hosts reach running then, though the leg had just run
in one, its line says nothing was dropped. A distribution that is not running is never started for
it, a drop that could not be made is said once in a wait - a WSL host whose item cannot be read,
where no other host reaches a distribution, and a list of the distributions running that cannot be
read, among the reasons - and one with nothing to drop says nothing. That list is read in UTF-16
too, which an older wsl.exe writes whatever `WSL_UTF8` says: read as UTF-8, each character of a
name came with a NUL, and the list was taken for one naming none.

Unlike a held lock, which refuses at once, admission waits - because the slots and room it waits for
come free as the legs ahead finish - but never silently and never for ever: while it waits the leg
says who holds each slot (tree, variant, host, leg, command, process, run, and since when it
asked) and, once it holds one, what the memory stands at, or the room free and who claims it; the
wait is measured on the monotonic clock. A leg that waited `maxWaitMinutes` (60, above 0) is
`not-admitted`, exit 7, naming what held the slots and the record they are kept in, the memory it
waited on, or the room, who claimed it and where the claims are recorded: nothing of it ran, and
nothing about the code is claimed. Every admitted leg's line names how long it waited, the memory it
started at and the room it claimed, and `--json` gives them, with the slots' record, as the leg's
`admission`.

## Leg integrity

A verdict is only worth reporting if it describes the code rather than the moment the
code happened to run in. Each rule here answers a failure measured in the scripts this
tool replaces, where a green result had quietly stopped meaning anything.

### Evidence that the work ran

- The exit code is read from the process itself, never through a pipe or a wrapper.
- Every test invocation declares a `successPattern`, checked on the platform section
  merged over `all` - a section found by the leg's `os` however it is spelled, as every
  per-system setting is read. It is matched against the runner's own output and never against
  anything the harness wrote: a log header that echoes the command line contains the
  pattern whenever the command does. An empty pattern matches anything and is refused
  when the file is read.
- A build passes only if every file in the project's `buildOutputs` exists afterwards,
  so a build that exited 0 cannot hand its tests a binary left over from an earlier one.
  An entry is a path, or a mapping of platform to path where the platforms disagree about what the
  same target is called — a program CMake names `app` is `app.exe` on Windows, and a static library
  differs by prefix as well as suffix. A bare string applies everywhere, so a list written before
  this means what it always did. An entry that names no path for a platform some leg builds on is
  refused when the file is read, naming the leg: a witness that is quietly not checked is the
  failure `buildOutputs` exists to prevent, and the legs and their operating systems are all known
  then. A suffix added automatically was the alternative and is weaker — it has to guess which
  entries name programs, and cannot express a name differing by more than its suffix.
- A ninja build's dependency records are read after it, with `ninja -t deps`: an object that
  recorded no header dependencies is never rebuilt when a header it includes changes, so it fails
  the leg, and records that cannot be read leave it `unmeasured`. Only an object built under
  `deps = msvc` - its build line's own, or else its rule's - can record none legitimately:
  `/showIncludes` reports headers and never the source, and ninja drops every header whose path, as
  ninja holds it relative to the build directory, names `program files` or `microsoft visual
  studio` as the system's own. So a unit including nothing, or only the standard library and the
  Windows SDK, records none, and so does one built from a precompiled header under `/Yu` that
  includes, besides, only headers the precompiled header holds and guards - with `#pragma once` or
  an include guard - which cl never opens again; one it holds unguarded is read again, and
  recorded. Such a zero is excused only where ninja rebuilds the object, all the same, for every
  header its compile surely includes: each header its command force-includes with `/FI`, and what
  its source and those headers include by a quoted include beside them, of a header ninja keeps -
  never inside a comment, and inside a conditional block only where the block is surely compiled.
  A block is surely compiled where its condition is a number, or asks whether `__cplusplus` is
  defined, which the unit's language answers - C++ under `/TP` or for a C++ source, C under `/TC`
  or for a `.c` one - and never where it asks anything else: one under `#ifndef _WIN32` may be
  compiled out. The command is the one ninja ran, evaluated as ninja evaluates it: CMake's C++ rule
  adds `/TP`, and each build line's `FLAGS` force-includes the header CMake precompiles, which for
  C++ holds its includes under `#ifdef __cplusplus`. An input a build line only depends on - an
  `OBJECT_DEPENDS` directory, say - is rebuilt for and never read. Ninja rebuilds an object for its
  build line's inputs and, for each input that is itself built, for what that build recorded and its
  own inputs - so a unit is rebuilt through its `.pch` for every header the object compiling it
  recorded, and while that object records none, neither it nor any unit built from it is excused
  for a header it holds. Only the object's own build line ever excuses it. A `msvc_deps_prefix` that stops matching what
  cl prints - a Visual Studio in another language - or a compiler cache replaying an object without
  cl's includes breaks only the objects rebuilt since, while older records stand: rebuilt with the
  prefix broken, two objects recorded `#deps 0` beside a precompiled header's object still
  recording its four headers, and read as proof that the build reads them, that record would have
  excused both; and rebuilt with it broken, a C++ precompiled header's object and every unit built
  from it recorded nothing and fail the leg. Under `deps = gcc` the source itself is always
  recorded, so zero is never legitimate there. How each object is built is read the way ninja
  reads the manifest: across the files `build.ninja` includes, since CMake keeps its rules in
  `CMakeFiles/rules.ninja`, with ninja's escapes undone, since CMake names each source absolutely
  and a drive's colon arrives as `$:`, and with its variables evaluated, since a compile's options
  arrive in them. Read from `build.ninja` alone, with the escapes
  left in, no MSVC object was ever excused, and a translation unit including nothing failed every
  MSVC build it was in.
- Every run has its own id, and every log is scoped to it. No two legs ever write to one
  file, so one leg's result can never be read as another's.
- The programs a command will start are resolved on the host before a leg starts - each command its
  own: a build its build's, a test its runner too, a run its steps' and its build's where it
  builds, a sync none - so a missing tool is `skipped-tool-missing` and named, not a failure
  halfway through. A program named by name, or by a path absolute on the leg's platform, is looked
  for beforehand; one named by a relative path or with a placeholder is the run's to find, and so
  is one started under an environment the configuration declares that sets PATH - looked for, so
  its directory reaches that PATH, and never required. One that will not start once the leg is
  running fails the leg, naming the program and the reason the system gave: never a skip, and never
  `poisoned`.
- A leg-running command asked for `--json` answers with its ledger whatever ended it: a command
  line the parser refuses, a refusal before any leg was placed, a selection no host could take, a
  log another run holds, a refusal while the legs ran - each is the document too, with the code
  the process exits with and the line it ends on. An interruption is said as one: `cancelled`,
  with exit 130, so a script never reads it as red. A machine that dispatched a leg reads the
  host's answer this way, so a host's refusal arrives as that refusal, in the host's words.
- Every other command asked for `--json` that stops before it has anything of its own to say
  answers in its own document's shape, never the ledger's: the agent verbs with the agent
  document, empty; `list-orchestrator`, `list-worktree`, `legs`, `install-missing-tools`,
  `check-ci-legs`, `check-anchor-citations`, `check-anchor-balance`, `fix-line-endings`, `validate-workflow`, `export-workflow`, `up`, `down` and `stop-run` with their own document
  holding nothing, after `exitCode` and `summary` (`ReportJson.Stopped`); the run verbs
  (`run-orchestrator`, `resume-orchestrator`, `answer-orchestrator`, `pause-orchestrator`,
  `stop-orchestrator`) with the flow document - the session they would have run, empty
  (`OrchestrationReports.FlowStopped`);
  `check-mutations --list` with its listing, no legs; `list-agent-providers` with its listing, no
  provider - and with `--template` with the template's own document, `template` null
  (`AgentProviderReports.Stopped`, `AgentProviderReports.TemplateStopped`); and `read-anchor` and `read-anchors`,
  whose document is an array, with that array empty - the process's exit code and the line it
  ends on say the rest. Each command registers which where it is wired (`CommandRunner.Wire`,
  `JsonAnswer`), so the plumbing answers for it without choosing by its name: where the parser
  refuses its command line, the first error the line it ends on, wherever the parser itself read
  `--json` as that command's own and set - never as another option's value, after `--`, before
  the command's name or given twice with a value each (`CommandRunner.UnparsedAsync`); where it
  stops the command itself; and where the body answers with no document - a usage error or a
  refusal returned before anything is weighed (the session's own report, `CommandSession.RunAsync`): a command asked for
  `--json` answers with one document on every exit save its usage and a `--json` given twice
  with a value each.
- One reader reads a command line's flags wherever the parser may have refused it
  (`CommandFlags`): whether `--json` is set, and each flag a stopped document says -
  `fix-line-endings`' `check`, `install-missing-tools`' `dryRun`, whether `check-mutations`
  answers with its listing, and whether `list-agent-providers` answers with its template. It
  never throws: a flag whose own value the parser refused, given
  twice with a value each, reads as not given. A command's body reads `--json` as that reader
  read it where the command was wired (`CommandContext.Json`), never from the option again.
- The one exit of a command asked for `--json` without its document is its usage: `--help`, `-h`
  or `-?` prints it instead, exit 0, whatever else the command line holds, even what the parser
  would otherwise refuse - the parser answers it before any command's action, its errors cleared.
- An emulated leg's emulator has passed its witness on that host before the leg starts.

### Inputs that hold still

Test suites read files from the tree while they run: configuration, corpora, fixtures.
Edit one mid-run and some tests see the old file and some the new, and the report
describes a tree that never existed. This was measured: eight failures, all passing
seconds later on the unchanged tree, because a configuration file was rewritten while
the suite ran.

A leg therefore fingerprints `test.inputs` (by default every file git tracks) before
its tests start and again after they end, on the host that runs them, by content and
size. Different fingerprints make the verdict `inputs-moved`. A fingerprint that could
not be taken makes it `unmeasured`: an unreadable snapshot is never reported as clean.
There is no escape hatch. A command that rewrites its own inputs is a build step, not
a test.

Between the two readings the inputs are watched, since two snapshots cannot see an edit
undone before the second. A watch reports what happens once it exists, except on macOS,
which numbers file events as it reads them: a write made a moment before a watch began
is sometimes delivered to it. There, each file the watch is told of is looked at as it
is told: one that stands as the tests found it - the same size, and written and created
when it was - was told of late, and counts for nothing; any other counts. Looked at then,
not once the tests are done, because a file moved aside and put back reads the same at
both ends and was something else while they ran. What goes unseen on macOS is a change
undone before word of it is looked at, and one of the same size undone in place by a
tool that also puts the old time back. The times are compared for equality alone, never
ordered.

The watch goes only as deep as the inputs: each directory holding one is watched for its
own files only, and each directory above one - the root among them - for the names of the
directories in it alone. A directory tracked for a placeholder, as init keeps the
worktrees root, `.orchestrators`, `.mutations` and `.legs`, costs one shallow watch, and
other trees building below it - an agent's worktree, a sweep's worker, a leg's copy - are
never heard. Measured: watched all the way down, an agent's build below the
worktrees root overflowed the operating system's buffer, and a run in the main tree that
only read its own inputs ended `unmeasured`. Past 64 directories besides the root's own -
each holding inputs and each above them, watched for names alone, counted alike - the
top-level directories watching the most of them are watched all the way down instead, until
the rest fit, each hearing the directories inside it through its own watch, and a tree whose
inputs lie in more top-level directories than 64 is watched whole, other trees' builds below
the worktrees root heard with the rest. Inputs in exactly 64 top-level directories still keep
a watch each. The bound is the watcher's, not the watch's: each one .NET makes is a handle,
a buffer and a thread, and on Linux, where it stands in, an inotify instance of a user's 128.

On Linux there is no such bound. Every directory that holds an input is watched for its
own entries, each above one for the names of the directories in it, and none all the way
down, through one inotify instance the process makes at its first watch and keeps until it
ends - or until reading it fails, which ends every watch on it `unmeasured`, and the next watch
makes another - read by one thread. .NET's watcher takes an
instance for each directory it watches, and measured, three legs at once in one
distribution held 120 of a user's 128; through the one instance they hold three. The
kernel gives one watch for a directory however often it is named, so spans watching the
same directory share it, and it is removed when the last of them ends; a span watching it
for names alone is told only of what the kernel marks as a directory. The instance is made
close-on-exec, so no program a leg starts holds it. The instance's calls are bound from the
C library by name - `libc.so.6`, then `libc.so`, then `libc` - and where none of them has
them, .NET's watcher stands in, as on Windows and macOS, and the process warns once
(`watch: WARN - inotify could not be called directly on this machine ...`). A limit met is
never a reason to fall back. All of this goes when the floor this tool runs on is a .NET
whose own watcher shares one instance (.NET 11); the seam it stands behind - which
directories to watch, decided once, apart from what watches them - stays, and so do the
rules below.

A directory counts for the inputs in or below it. One that holds inputs, or lies above
some, going, arriving or changing its name counts every input in or below it, under both
names of a rename: each went or arrived with it, and a directory moved aside and put back
reads the same at both ends, as a file does. It is heard on every system however deep the
inputs lie, since the directory it lies in is watched for the names in it - the root
among them, so a directory of inputs that is not there when the work begins is heard
arriving. A directory merely written to - as one is each time a file is made in it -
counts none, or a build writing beside sources would count every source above it. On
Windows a directory above a watched one cannot go or change its name while the watch
holds it: the system refuses whoever tries, as it does any directory with a handle open
below it, so there the rule holds by the move never happening.

A watch that cannot start stops the work before it begins. The leg ends `unmeasured` at
once, exit 3, with no phase run - a runner's segment not begun, a build's record not
written - saying `the inputs could not be watched, so none of this work was run:` and the
machine's reason, and whatever the watch had taken is released before the refusal leaves.
Run first and called `unmeasured` after, as it was, the whole of the work was spent to
learn what was known before it began. A build that had decided to start from clean has
deleted its directory by then; the decision stands for the next build. A watch that
started and is lost while the work runs - its buffer or its queue overflowed, its
directory gone - still ends the leg `unmeasured`, as the work ends. The remedy for a watch
that could not start is the machine's, not the tree's settling: free or raise what the
reason names, or put back the tree the watch was to be put on, then run again. A run
check whose own runner's watch cannot start was not run, and confirms nothing ([The
gate](#the-gate)).

On Linux the reason can be one of two limits, each said with what `/proc` shows of it:
this user's inotify instances all taken - `fs.inotify.max_user_instances is 128, and
/proc shows 128 held: 51 by pid 4242 (dssharness), ...` and `This process needs one, however many legs it runs.` - or its
watches run out - `fs.inotify.max_user_watches is 8192; this work watches 3120
directory(ies) - those its inputs lie in and those above them - one watch each, and /proc
shows 6011 held: ...`. The holders are this
user's processes, the most first, three named and the rest counted; where `/proc` cannot
be read the sentence ends at the limit. The leg does not wait for an instance, and
admission does not count them: at one instance a process this tool is not what holds
them, and another program's hold ends on no schedule this tool can read. What the person
can act on is who holds them, which is what the leg says.

A leg on another machine tests that machine's copy, and a leg here that works in a copy of
its tree tests that copy; each holds still under it, and what can move is the tree, before the
copy is made of it. So a run reads each tree its copies are made of - its hosts' and its legs'
own here - once, as it begins and before any leg's work, and every copy is made from
that reading, as [Syncing a tree](#syncing-a-tree) says: an edit put back before a host's
sync comes round leaves no trace there, and where a file to be carried no longer holds what
was read - edited, or removed - the legs on that copy are `inputs-moved`, naming it, with
nothing of them run, while the legs on other copies still report. A reading confirms itself: a
file written, appearing or going while the tree is being read does the same to every copy of
it, naming the path. The run says which commit it read the tree at, and each leg on a copy says
it again with its verdict. Measured: an edit a mutation-testing
tool left in the tree for 7.3 seconds of a run, and put back, was built and tested on a Mac
and reported `failed`, while the tree was the same at the run's start and end. A run with
`--use-staged` reads nothing, and tests whatever the copies hold. A leg that works in its tree
itself on this machine is held to its own guards instead, which watch its inputs while it builds
and while it tests: an edit made after the run began and left in place reaches such a leg that
starts after it, which builds and tests the edited tree from start to end, never a mix of two -
so one run's legs in their trees here and its legs on copies can have tested different trees.

### Clocks are never trusted to order anything

One host this tool must serve has a wall clock that steps forward by about 25 seconds,
for about 200 milliseconds, every few seconds, and the steps reach file modification
times: a file written one second after a marker carried a timestamp 24 seconds before
it. So:

- Nothing decides that something changed by comparing two timestamps taken at different
  moments or on different hosts. Change is detected by equality, as above, which a clock
  cannot distort because both readings carry the same distortion. Sync decides what to
  delete by comparing manifests, never by stamp order. Where dates are still ordered, it
  is to ask what a build system that orders them will do with a change already found by
  content, below, or which of two files CMake wrote in one configure came first.
- Durations come from the monotonic clock. UTC times are for display only.
- Each phase compares elapsed wall-clock time with elapsed monotonic time. Drift beyond
  `defaults.clockStepToleranceMilliseconds` records a clock step or a host sleep inside
  that phase: its durations are suspect, and so is every timestamp it wrote.
- Incremental builds are protected from it. Ninja, Make and MSBuild decide what is
  stale by ordering timestamps, which a stepped clock defeats without a word: an object
  stamped during a forward step looks newer than a source edited just after it. In each
  variant's build directory the harness keeps a record of the build that last ran there:
  a content fingerprint of the inputs the build system was given, and when each had last
  been written, taken before it runs, so a build that fails or is stopped by its caller's
  time limit leaves the record of what it compiled from. A phase that spans a clock step
  marks the record at once; the end of the build writes it again with the newest file the
  build left, and marks it unordered, with why, if anything doubted it: inputs that did
  not hold still, a directory something else used, an input that could not be read.
  Before building again, if the record is marked unordered, or an input whose content
  changed since is dated no later than the newest file that build left, the variant is
  rebuilt from clean and the ledger says why, naming the file and both dates; so is a
  directory that holds files and no record, as a clean start stopped part way through its
  delete can leave one, since nothing says what they were built from. The newest
  file, not the declared outputs or the record: a step forward and back inside one phase
  measures no drift, and an object compiled in it is dated ahead of the binary linked
  after. What the build left, not the directory as it stands: a test run writes there
  too - ctest its logs as a suite ends - and an edit made while the suite ran would be
  dated behind them. After a build that never finished, which recorded no newest file and
  whose guards never said whether its tree held still, the directory is read as it
  stands, and an input written again since it began counts as changed though its content
  held: a stash and its pop leave one exactly as it was, having let the compiler read
  something else in between. A stale binary reported as a pass is the one price an
  incremental build must never pay. An input changed and dated after all of that is the
  build system's to act on - newer than every output, it rebuilds what reads it - as is
  one deleted since, which a build system sees gone without asking its date, and a CMake
  project is configured on every build. What a build system does not know reads a file -
  a custom command's input it names in no `DEPENDS` - is not remade. Rebuilding from clean
  for every change put a consumer through 1,186 steps from nothing for one edit to one
  input, and a build stopped for running long would have started from clean again every
  time. The record keeps `clock-stepped` on its first line for an unordered build,
  and each input's fingerprint on a line of its own, as 0.5.8 wrote and reads them.

### One run per build directory

The lock (see *Locking*) keeps two harness runs apart, but a lock cannot see a tool
someone starts by hand. Measured: a test run started in a shared build directory
while a gate ran turned a green suite red, with four test processes live at once.

- Each leg samples the process table when it starts, when it ends, and every
  `defaults.processSampleSeconds` between. A `contention.buildTools` process outside the
  harness's own process tree whose command line names the leg's build directory makes the
  verdict `contended`. A `contention.sharedResourceTools` process, one that shares a cache
  rather than a build directory, is reported as a warning, one line per tool and per whose it
  was: a process whose command line names another leg's build directory is that leg's, and one
  naming a build directory of another tree of the repository on the same machine - a
  worktree's, an agent's, the main checkout's, each listed as the leg's work begins there, its
  directories reckoned by this tree's configuration - is that tree's leg's ("worktree o1/xa's
  leg 'linux-debug'"). A consumer's agents, each testing its own worktree on the same machines,
  were warned of one another's test processes as nobody's - 1510 beside one leg - on every run,
  until nobody read the warning. Only what no tree there accounts for is nobody's known, and
  where the other trees could not be listed its line says it may be one of theirs, and why. A
  build tool is a contender only where it names this leg's own build directory, which no other
  tree's directory is or holds, so another tree's build never makes a leg `contended`.
- Every sample is kept, and the report says when each process was seen: throughout, at
  the start, or at the end. A process table that could not be read is reported as
  unknown, never as nothing found.
- **A leg no sample could read the process table for is `unmeasured`, not passed.** The
  platform's own source is the only one that carries a command line, and a command line is the
  whole of what contention is decided by, so a machine whose query is blocked by policy would
  otherwise report "no contender" for every leg, for ever, without a word. One failed reading
  among several is a stated limit rather than a verdict: the samples that succeeded did look.
- No verdict depends on a sample finishing within a time window. Sampling costs
  seconds on one platform and a fraction of that on another, and one such overhead
  asymmetry was once read, for a whole cycle, as a speed difference between legs.
- **A phase is bounded by silence, and silence starts when the child does.** Reading the request,
  opening the log and starting the process are this tool's own time; counting them against the
  child made a slow launch on a loaded machine read as a hung command. A child that starts and then
  says nothing is still bounded, because starting is itself something the clock is told about.
- A process is identified by its id together with a stamp that tells it from the next
  holder of that id, and a parent link is followed only when the parent started no later
  than the child. Process ids are recycled: on Windows a freed id was measured coming back
  after about a hundred allocations.
- **That stamp holds no clock.** On Linux it is the boot this machine is on and the tick
  within it the process started, read from `/proc`; on Windows and macOS it is the start
  time the kernel records once at creation and never works out again. A start time
  recomputed from the current clock — which is what `ps lstart` reports, and what adding
  `/proc/stat`'s `btime` to ticks-since-boot produces — moves for every live process the
  moment the clock steps, and every live holder then reads as a recycled id at once. On a
  host whose clock steps by about 25 seconds every few seconds, that is not an edge case.
- What sampling cannot see is stated in the report, and said under `--verbose` one line
  each, as `sampling cannot see` and the limit. Two hold on every machine: a tool started
  from inside the build directory by a relative path, since no process's working
  directory is read; and the command line of a process that does not expose it,
  so such a process is counted and never matched against the build directory. Where no
  process sampled had a command line that could be read, it cannot see any command line
  on this machine, so nothing was matched against the build directory; and where some
  had no start time, when some processes on this machine started, so a process id
  reused during the leg is reported as one process.
- Selecting two legs that resolve to the same build directory is refused before either
  starts.

### Legs that stay comparable

- Core counts are handed to every runner explicitly, through `coresEnv` where the runner
  reads a variable and `coresArgs` where it does not. A runner left to its own default
  runs serially on one host and on every core on another. A variable is preferred: an
  explicit option in the invocation's own `args` still wins.
- A leg's environment is carried to its host explicitly. WSL passes on only the
  variables named in `WSLENV`, and ssh passes on none.
- `countPattern` extracts how many tests each leg ran. Legs running the same tests - the same
  project, and the same `testSet` - that report different counts are marked, among at least
  three: a platform that quietly skips a group of tests passes on less evidence than its
  siblings. The mark is its own part of the leg's line, `test count differs: it ran 2238
  test(s), where 2 other leg(s) ran 2237`, and its own fields in `--json`, `testCountDiffers`
  and `testCountNote`, beside the `project` and `testSet` the count belongs to. It is never a
  timing mark - a Windows-only test once read as a Windows leg's timings being suspect - and
  never changes a verdict.
  - **A difference that is expected is declared.** A test invocation naming a `testSet` - a
    platform's own tests, a sanitizer leg's subset - is compared only with the legs naming the
    same one, and every other leg of the project with the rest. A leg of another project runs
    another suite, and is compared only with that project's legs; a count whose leg resolves no
    project - which only `test --no-build` can reach - is compared with nothing.
  - **A count is recorded with what it belongs to where it is made**, on the host that ran the
    leg, and read back with it. A host running what it has staged may have run another set than
    this machine's configuration now names, and is compared as it counted.
- The ledger reports command time and harness overhead (sync, fingerprints, sampling)
  separately. A phase slower than `defaults.durationWarningFactor` times the same phase
  on sibling legs of the same kind is marked suspect. A timing mark never
  changes a verdict. An emulated leg is never compared with a native one.
- `keepAwake` holds a host awake while a leg's own work runs there. A host that slept once
  reported a 4 millisecond test at 729 seconds. The command is started on the machine that
  runs the work - by the DssHarness on a host a leg was dispatched to, under that host's own
  section - with `{pid}`, the one name it is filled in with, replaced by the DssHarness process
  running the leg, and it is stopped when the work ends. `["caffeinate", "-dimsu", "-w",
  "{pid}"]` on macOS also stops by itself should that process end first. A command that cannot
  start, or ends early, is said and fails nothing: a sleep it did not prevent is still seen, as
  wall time outrunning the monotonic clock, and marks the phase it interrupted suspect, as it
  does on a host that declares no command at all. The survey asks about the command, so a
  directory it is found in reaches its PATH, and turns no leg away for it.
- `holdAwakeSeconds` holds an ssh host awake between commands, until a command's own keepAwake
  takes over there. Every keepAwake a command starts on a host ends with the connection that
  started it, and a personal Mac falls back asleep in the seconds before the next command, which
  then cannot find it. So a
  host that declares it is recorded as each command reaches it, and asked, as the command ends -
  however it ended - to hold itself awake that long. The DssHarness there records the hold and
  starts a process of its own, detached from the connection, which runs the host's keepAwake with
  `{pid}` filled in with itself and goes on once the connection has ended. The next command's own
  keepAwake ends the hold there, as it starts; a newer hold replaces an older one; a host whose
  DssHarness is to be updated has its hold ended first, since the hold is a DssHarness running
  there and no update replaces one that runs; and a hold ends by itself when its seconds are up. The hold's process watches its record rather than being
  stopped by an id, which a process started since could have been given, and the record is kept
  among the user's own application data, one per user of the host. A hold that cannot be left is
  said, and fails nothing. On a Windows host, OpenSSH may end the hold's process with the
  connection.
- A host's compiler cache is that cache's own variable in the host's `env` - `CCACHE_DIR` for
  ccache - so two hosts never share one store, and a build keys it against the leg's own tree.
  The `compilerCacheDirectory` key that once said the same is retired, and refused where it is
  read, naming `env` instead.

### Hosts and trees

- An ssh host bounds how long a connection may take to open and how long it may go
  unanswered (`connectTimeoutSeconds`, `keepAliveSeconds`). Without both, a dead link
  hangs a leg indefinitely, with no output and no verdict.
- A host's copy of a tree is a git repository sync creates: the main checkout's at the host's
  `repositoryPath`, and each worktree's beside it, at `<repositoryPath>.worktree-<name>`, named for
  the worktree's directory as a worktree's name is spelt - an orchestrator's agent's for both its
  orchestrator's name and its own, joined by two hyphens, which no worktree's name can hold, so two
  orchestrators' agents of one name keep their copies apart. One copy per host had every worktree whose
  legs reached a host wait for every other's, under one lock, each sync replacing the tree the one
  before had put there. Beside the main copy rather than inside it, because the agent a sync starts
  begins in the copy's parent, which must already be there, and a copy inside another would be taken
  for part of it by git. This machine records which hosts hold a copy of which worktree, and of
  which tree on this machine, in `.harness-config/host-copies` in the main checkout - as the lock is,
  at a place no branch's configuration moves - which ignores itself, as the runs directory does, so
  git never sees it and no sync carries it. A sync claims its copy there before it writes anything,
  so a first sync that stops part way is recorded too, and is refused one another worktree of the
  same name - made by hand, or by another tool, outside the worktrees root - still holds: synced by
  both, each would replace the tree the other put there. Deleting a worktree asks each host that
  holds one of its copies to remove it, only where the harness made it, and no other host - and
  first the mutation workers kept in it, asked about before any goes, as in the worktree itself:
  one a sweep still running there holds keeps the copy, still recorded, as does one that cannot be
  removed or told, and a copy kept once some had gone says which went (see *Mutation testing*). A
  host is
  reached through the worktree's own configuration, read before it goes - its branch may declare a
  host the configuration the command runs in does not - or else through that one; a host neither
  declares is not asked, and its copy is forgotten, named. Each copy is removed under the lock a leg
  this machine runs there takes, taken with everything kept inside the copy (`RunLock.RemovalCommand`,
  on `HostCopyRemover.RemovalLock`), so a sweep this machine runs there - its key in the copy's
  `.mutations` - keeps the copy as a run in it does, and the dry run of a deletion names it as one,
  while no sweep takes a key there once the removal holds the copy;
  the record is read again once the lock is held, so a copy another worktree
  of the name has claimed since is left for it; it is forgotten before that lock is let go, and its
  marker goes last, so a removal that stops part way leaves the rest marked as the harness's. The
  host is asked from its home directory, which is there when the directory the copy was kept in is
  not, so a copy whose directory is gone is answered as not there, and forgotten.
  A copy that cannot be removed then stays recorded, and the deletion fails naming it though the
  worktree is gone, with the highest code a copy was left with - 13 where a run or a sweep holds one or it was
  refused, 15 where its host is unreachable, 20 where the removal failed there - so whoever deleted
  it learns something of it is left; deleting the worktree again finishes the job, and for a name
  whose worktree is gone removes what any worktree of that name left, never the copies of one that
  still exists. `list-worktree` shows that record: each worktree's copies, and the copies left by
  worktrees that are gone - removed by a tool that ran plain `git worktree remove`, say - each with
  the `delete-worktree` that deals with them. With `--hosts` it asks each declared host which
  `<repositoryPath>.worktree-<name>` copies it keeps and how large each is, from its home directory
  as a removal is, and sets them against the record, so a copy the record does not hold - made from
  another checkout or machine, or forgotten here - is found, as is a recorded one that is gone. It
  changes nothing on a host or in the record, so it takes no lock. On a host a leg was sent to,
  the copy it was sent to is its tree, whatever worktree the leg names. The working tree being tested
  is transferred into its copy file by file, compared by content hash, so what the host holds is this
  tree including its uncommitted changes. Nothing is pushed and it is never
  a clone from a remote, either of which would need credentials on the host and neither of which
  could carry a change nobody has committed. It is made a git repository because the host's
  DssHarness finds everything through git, and sync never writes into a directory it did not
  create, because it deletes whatever the source does not have.
- A remote tree's identity is its content manifest. Every sync that finishes confirms the copy
  equal to the reading of the tree it was made from - in a run, the reading taken as the run
  began; one that stops because the tree moved confirms nothing, and no leg runs on its copy.
  The ledger records the commit and manifest each leg built, and a build directory produced
  from a different manifest is flagged.
- A build directory's recorded source directory must be the leg's own tree, such as
  CMake's `CMAKE_HOME_DIRECTORY`. A build directory configured from a different worktree
  is refused, not reused: watching the wrong tree produced both a false refusal and a
  silent wrong answer.
- Commands run as argument lists, never through a shell, so no shell's process
  emulation sits between the harness and a runner. MSYS's emulation was measured losing
  tests from a parallel test run with no failure reported.
- A program the process runner starts that will not start because something holds it open for
  writing is started again, every 20 ms for up to two seconds (`ProcessRunner.StartAsync`), before
  that is its failure. Linux starts no program so held, and a process forked while the program was
  being written - by any thread, for a child of its own - holds it until it becomes the program it
  was forked for. Met in this tool's own suite, on a hosted Linux runner: a script written, closed
  and started at once failed with "Text file busy".

## Cross-leg contamination

The hazard: two legs sharing state and silently corrupting each other's results.
Seven independent guarantees, each addressing a measured failure mode:

1. **Variant-keyed build directories.** `<tree>/build/<processor>-<toolchain>-<config>[-<sanitizer>]`.
   CMake refuses a compiler change on an existing cache, so `msvc` and `gcc`
   cannot share `build/release`, and neither can a native build and a cross-build.
2. **Tree-rooted paths.** Every path derives from the leg's tree root, so a
   worktree's build output can never land in the main checkout's.
3. **Build directory guard.** Before configuring, `CMakeCache.txt` is read and
   the run is refused if `CMAKE_HOME_DIRECTORY` or the recorded compiler
   disagrees with this leg. The compiler is compared by the file it starts: the name the
   leg's toolchain gives is resolved on the PATH the build's phases are given - its
   environment's own, or this process's, with the directories a survey found programs in
   around it - and held to the whole path CMake cached. A name compared with a name let a
   directory configured with one gcc be rebuilt with another earlier on the PATH, and the
   leg reported on objects from both. A name the search finds nowhere cannot start, and is
   compared by name until the build says so.
4. **Per-host compiler cache.** `CCACHE_DIR` and `CCACHE_BASEDIR` are set
   explicitly per host rather than inherited, so hosts never share a store.
5. **Clean run directories, incremental build directories.** Scratch and run
   directories are wiped before every leg; build directories are preserved.
6. **Locking.** See below.
7. **One sync per tree.** Legs sharing a host's tree share its single sync instead
   of each writing the same files at the same time.

### Incremental builds across a transport

Existing tooling forces a clean rebuild after every remote sync, because
`rsync -a` and `tar` preserve mtimes: a synced source whose mtime lands behind
an existing object file makes Ninja skip the rebuild and report a stale binary
as success.

`DssHarness` syncs by **content hash**, writing only files whose content actually
changed. An unchanged file is not touched, so its mtime does not move; a changed
file is rewritten now, so its mtime advances. Ninja's incremental check is therefore
correct after a sync, and incremental builds are preserved on every host.

That holds while the host's clock is honest. *Clocks are never trusted to order
anything*, under Leg integrity, covers what the harness does when it is not.

### Locking

`.harness-config/lock.json`, always in the **main checkout** (a worktree has its
own `.harness-config`, so a per-tree lock would make two runs of the same leg
invisible to each other). Gitignored.

One entry per `(host, tree, variant)`, recording host, pid, process start
time, run id, UTC timestamp and the command. A host's copy of the repository is locked
by the DssHarness on that host, in that copy's own `.harness-config/lock.json`, so runs
started from two different machines against the same host see each other.

Two granularities, because two kinds of work share a tree. Syncing a tree takes the
tree exclusively, since it rewrites files every variant reads. Building or testing takes
the tree shared and its own variant exclusively, so variants build side by side but never
while their sources are being replaced. A lock is released only by the run that took it.
A sweep of a leg's mutation arms builds in workers of its own, never in the tree, and takes
their key exclusively instead - spelt as one more copy in the tree's `.mutations`, which no copy is - so
it meets another sweep of the leg's variant and a `clean` of its workers, and never a build,
test or sync of the leg (see *Mutation testing*). A leg that works in a copy of its tree holds
only the copy: its sync takes `'<tree>/.legs/<key>' on local` whole, and it then holds that copy
shared with its variant, as a host's leg holds the host's copy - so a fold, a rebase, a refresh,
a commit or an edit in the tree meets no lock of the leg's, and the leg ends on the tree its run
read. A fold that lands while the run is still reading the tree ends the legs on its copies
`inputs-moved` with nothing run - seconds, at the run's start - and the same run started again
reads the folded tree.

- A held lock **refuses immediately**. It never waits: silently blocking for
  hours is worse than a refusal that names the holder.
- A tree on this machine is **one key however it is reached** (`RunLock.Key`): its full path with
  every link and junction along it followed, as git spells a tree, told whenever the lock is read -
  by a run, a dry run, a clean or a deletion alike - for each entry and each request, once a
  reading. An entry keeps its tree as its run spelled it, so a build before this one, which
  compares trees as spelled, still meets what it spelled alike, and this one meets whatever it
  spelled: a worktree kept on another disk through a worktrees root that is a link, or a copy kept
  through a `.legs` or `.mutations` that is one (`init` allows it), is spelled through the link from
  the main checkout and as git resolves it by a run inside it; compared as spelled, a deletion, a
  clean or a sync asking for a copy one way missed a run holding it the other, and removed or
  replaced it under that run. A request whose links cannot be followed is refused before anything
  is written, never keyed as spelled; a host's tree, whose links this machine cannot follow, is the
  key it is spelled as. An agent's trees are taken as git names them (`AgentService.Holds`), as
  builds before this one took them.
- A **removal** takes the tree with everything kept inside it: a tree taken whole under
  `delete-worktree` (`RunLock.RemovalCommand`), as every removal of a host's copy is recorded, by
  this build and those before it, rather than by a field of its own, which an older build would
  refuse the lock file over. Asked for, an entry on that host for anything inside the tree - a
  sweep's key in a host's copy - is in its way, as one for the tree is; standing, its entry is in
  the way of whatever is asked inside the tree, so no sweep starts in a copy while it is removed.
  A sync, a build or a test of the tree still meets only what is on the tree itself.
- A holder is named by its process, run and start - `pid 12, run R, since T` - never by its
  machine's name, which only its record keeps, to tell a holder here from one elsewhere: a host's
  lines reach the machine that sent it work, and wherever that machine's output goes, and a
  consumer's display had to mask a host's name in every one. One recorded on another machine is
  said to be - `pid 12 on another machine, ...` - unnamed; a heavy-leg slot, every one of which is
  its machine's own whatever name it carries, never is.
- Staleness is decided by **liveness, never by a timeout**. A timeout is a guess
  about how long honest work takes, and it eventually breaks an honest run.
- A dead holder on this host is reclaimed automatically, and the reclaim is
  reported. A holder on another host requires `--force-lock`, which is always a
  human decision.
- A clock-free process stamp is recorded alongside the pid so a recycled pid is not
  mistaken for a live holder, and so a clock that steps cannot turn a live one into a
  dead one. Compared exactly: there is no clock in it for a tolerance to absorb - save
  that Windows and macOS hand a start over in the machine's time zone as it is now, so a
  machine whose zone changed reads a live holder's start whole quarter hours apart from
  the one recorded, and that is read as the same start.
- `--force-lock` takes any lock actually in the way, on this host or another. On this
  host it is the only way out of an id that has come back around to something live,
  which would otherwise hold a tree until the file was edited by hand. It takes the log
  path with it, for the same reason.
- A lock file, or the file that records who owns a run's logs, that cannot be read or written
  refuses the run, exit 13, naming the file - a runs directory an earlier run under sudo left to
  root is the usual cause. It stops every run on every tree alike, so it is never reported as
  each leg being locked by a run that does not exist, nor as a defect in this tool.
- A lock, or a log path, that cannot be given up once its work is done is a warning naming it,
  and the work's verdict stands. The entry names a process that has ended, and is reclaimed as
  a dead holder's is.
- A run killed, or stopped with its machine, before it finished writes no verdict and gives up
  nothing, and nothing would ever claim its log path again, every run having its own. So a run,
  once it owns its own path, releases every path beside it that a run on this machine holds
  whose process has ended, and says each was abandoned - its run id, process and start, and
  where its records are - most likely killed, or stopped with its machine, unless that run
  warned as it ended that it could not give its path up. A holder recorded on another machine
  is left as it is, unsaid; one in a record this build cannot read - a newer build's, or one its
  machine stopped while writing it - or cannot reach is left as it is too, and said.

### Where a run's records live

A run's records - its logs, and what it has already completed - are kept in
`.harness-config/runs/<run id>/` of **the tree that ran it**, a worktree's own included, so a
worktree reads what it judged without leaving it. Kept in the main checkout instead, as they
once were, a worktree's runs landed beside the main checkout's. Nothing in `runs/` is shared
between runs: each writes only the directory named by its own id. What two runs from different
trees contend over is the lock above, which stays in the main checkout.

A run is stopped from another terminal by `stop-run <run id>`, typed in the tree that ran it: it
writes `<run id>.stop.json` beside the run's directory, as its owner file is (`<run id>.owner.json`),
which the run's dispatcher looks for every `StopCadence.PollEvery` while it owns the directory, takes -
recorded taken among the run's own records (`stopped.json`, `RunStopTaken`, `RunStops.TakenFileOf`)
and only then the request removed - and stops on as an interrupt stops it: every leg stopped where it
stands, its ledger written with what was reached, exit 130, a line naming who asked. A stop is taken
only where its taking is recorded: one whose record cannot be written, or whose request cannot be
read just then, is said once in the run's terminal and stays asked for the next look, so a run never
stops while whoever asked would hear that nothing was stopped. Each file is read as its own shape: a
request that does not read as one is said and removed, and stops nothing; a record that does not read
as one is said, and is no stop taken. `stop-run` waits 60 s for the
dispatcher to give its directory up, and reads the run's record: there, the run stopped (0), naming
it; not there, the run ended on its own before its dispatcher took a stop, so nothing was stopped
(13). Every exit names the stop record that stands - the request while it is asked, the run's record
once taken - and never a request the command took back. The record is the run's, not an asker's,
so two terminals asking at once, or one asking again while the run stops, all hear the same. The dispatcher's watch ends before its directory is given
up, and gives it up however it ends (`RunStopWatch.EndThenAsync`), taking back a request it never
took, so no stop is ever taken by a run that has ended. Not
stopped in time, the request stays asked (13), and `--now` ends the dispatcher's process where it
stands with its whole tree - only one of this machine, and only the process its owner file names by
its start - and releases its claim; its ledger is never written. A run that has ended, or whose
dispatcher went without giving its directory up, is nothing to stop (13), and an abandoned claim is
released - one killed while the stop was waited for too, said stopped (0) where it had taken the
stop first; a request nothing will read any more goes with the claim of the run that left it,
whoever finds it so (`RunStops.ReleaseAbandoned`, `RunStops.ReleaseAbandonedBeside`). How often a
dispatcher looks and its asker watches is every such command's one cadence (`StopCadence`), an
orchestrator's included. What a stop's `--now` answers where ending the process left nothing more to do is
worded once for it and `stop-orchestrator --now` (`ProcessHolders.NothingMoreToDo`), and an
interrupt while it waits for the process to go is the caller's, exit 130. `stop-run` is never asked
of a host - the dispatcher's stop is what stops its legs on every host - and never run by an agent.

The directory ignores itself: the run about to write there first gives it a `.gitignore` of its own,
holding `*`, so its records never show in git status, whatever the tree's `.gitignore` says. Kept on
another disk through a link, it is the link git sees, and never what is beyond it: the managed
block ignores it by name, so the link is ignored too. A
worktree of a branch that predates the harness holds no rule for it, and its records were committed
by the next `git add -A` and made `delete-worktree` refuse over the harness's own logs. No sync
carries them, as none carries any of the harness's own state; deleting a worktree deletes its runs
with it; and a run is resumed from the tree it was started in. A caller never works the directory out:
`build`, `test`, `run` and `check-mutations` name it on every exit that created one, as
`logs: <directory>` and as `runDirectory` in `--json`, and a sweep's records of each arm are in it
too (see *Mutation testing*). Each names its run from its first line, `run <id>`, and as `runId` in
`--json`, on every exit: the run is begun before anything can refuse it, so a run refused before
it had a directory - a leg nobody declared, a selection no host could take - is named too. It
keeps no records, and its id is all there is to cite it by. A command line the parser itself
refuses never started, and names none. Each leg that kept records has a `logs of` line of its own,
said as its verdict is and again in the run's closing lines - the same sentence, one owner
(`LegRecords.Said`) - naming the leg's own directory and the phase logs in it by name, in the
order of their names; `--json` holds them as the leg's `records` and `logs`. A leg that kept none -
refused, not admitted, skipped - has no line. A leg another host ran was run there under a run of
its own: the dispatcher names it once, `<leg>: run <id> on <host>`, never relaying the host's own
run line (a later one of that shape is a program's own output, and is relayed as it came), the
leg's `--json` holds the id as `hostRunId`, and its records and logs arrive as the host spelled
them, its home written as `~` (see "A host's home is `~`").

## Disk space

### Removing a build directory

`clean` removes each selected leg's build directory where the leg runs: in this machine's
tree, or in the copy of the tree a WSL distribution or an ssh host holds. It is for a disk a
build filled, where nothing else helped: a build that starts from clean fills it again as it
goes, and deleting a worktree takes its local tree too. So it writes nothing on the machine it
removes from before it has removed - no sync, no lock entry, no run records.

- **The lock is read, never written.** A leg is kept from a build of it by the lock that build
  takes, keyed the same way - host, tree there, variant - on the machine the command runs on and,
  for a leg on a host, by the DssHarness there in that copy's own lock file. It is read under the
  machine-wide mutex a run needs to write it, and while nothing holds it the directory is renamed
  aside, so no run can take the lock between the reading and the renaming. A held lock refuses the
  leg, `refused-locked`, naming the holder. An entry of a run that has ended is passed over and
  left in the file: taking it back would write the file.
- **Renamed, then removed.** A rename writes no file's contents, so it needs no room the disk
  does not have, and it takes the directory out of a build's way at once; the removal, which
  can take minutes, happens outside the mutex. A build started meanwhile starts in a new
  directory. What an interrupted removal left aside, hidden beside the build directory, the next
  clean of that leg removes first.
- **A link is left alone.** A build directory that is a link was put somewhere on purpose, and
  removing the link would free nothing and have the next build fill this disk instead.
- **Said per leg.** Each leg's line says what was removed and the room left on its filesystem,
  or, with `--dry-run`, what it holds, removing nothing; `--json` carries both as the leg's
  `space`. A dry run ends as the clean would: a leg a run is building, whose workers a sweep
  holds, or whose copy a run holds, is `refused-locked` in the dry run too, exit 13, the holder
  named - with what each holds still said, and still measured, for the dry run asks every
  question the clean asks and moves nothing. A leg on a host is asked of the DssHarness there, in
  the copy - once the copy is known to be there, so a tree never synced to a host has nothing
  removed rather than a host refusing it, and asked here first on a dry run as in a clean, so the
  host is sent nothing a run of this machine's holds off.
- **A leg's mutation workers go with it.** The workers its sweeps keep in its tree are removed
  the same way, and first, so the room its line says is the room once they are gone: its own
  variant's, its self-test's, and those of a variant no leg of that host and tree builds any more,
  which nothing else would ever remove - another leg's are that leg's. Each variant's go under the
  lock a sweep of it takes rather than its build's, and are said on the leg's line with what they
  held, which `--json` carries as `space.workerBytes` - what was removed of them, 0 where every one
  there was kept, or in a dry run what is there - wherever anything named as a worker of the leg's
  was found: a leg whose build directory was not measured, a link, locked or not removable,
  carries a `space` saying its workers alone. A worker a live sweep still claims is kept, the leg
  `refused-locked`, as is one whose claim cannot be read; one that cannot be removed fails the leg,
  whose line still says what went, and counts it; one whose sweep died holding it is released
  first, and said; and a directory under a worker's name that no sync made is said and left. A host
  holding no copy of the tree holds none of its workers either, since they were kept in the copy:
  it is asked nothing. What `clean` measures of a build directory never reaches a slot.
- **The copies its legs work in go too.** The copies of the tree kept in its `.legs` slot are
  removed the same way - the leg's own variant's, and those of a variant no leg builds any more,
  whatever the configuration says now, for a copy made under a setting since changed is still the
  leg's - each under the lock a run takes on it, so one a run is building in, or replacing, is
  kept and said, the leg `refused-locked`. `--json` carries what they held as `space.copyBytes`,
  beside the workers' `workerBytes`, and a leg whose build directory was not measured carries a
  `space` saying its workers and copies alone, where any is there. `delete-worktree` removes them
  with the worktree, asked about before anything of it goes, as it asks the workers.

A host whose DssHarness is older than this machine's is brought to this build first, as it is by
every command that asks it anything, and that write needs room. A host that is both full and
behind is freed by hand, once.

### Room before a build

A leg is placed only where its host has the room its build still needs, as it is only where the
programs it starts are. A consumer's two variants - the first builds of a new worktree's copy -
filled a host's disk half way through and died writing an object, while `legs` said the host
could run them. One command counts only its own legs; where a machine declares admission, each
heavy leg also claims its room as it is let start, held against every other command's legs there
(see *Room is claimed as a leg is admitted*).

- **What a build needs** is what its build directory comes to once built: the leg's
  `buildSpaceGiB`, or, left out, what a build of its variant recorded as it finished - in the
  tree's own copy on that host, or else the most any other copy of the repository there recorded,
  naming whose - less what the directory already holds. A build records what its directory came
  to in its `.harness-build`, summed from the walk it already makes of the directory as it
  finishes. The other copies are the main checkout's and every worktree's, an agent's or a plain
  one, and each tree's leg copies after it: a consumer's leg, placed where neither its own tree's copy nor the main checkout's had built
  its variant, filled a 47 GiB disk under two other legs while three worktrees' copies beside them
  had each recorded about 11.4 GiB. The most of them, since one variant comes to about as much in
  every tree, and a need said over keeps a leg out until there is room where one said under fills
  the disk. On this machine they are the main checkout and every worktree git records; on a host
  this machine sends legs to, its main copy and each worktree's copy this machine's record of host
  copies holds there; and a host running a leg it was sent, which places it again, finds the
  worktrees' copies beside its main copy itself. Trees that could not be listed are said, with why,
  and the main checkout's copy still counts.
- **Nothing is walked to decide.** Each host is asked, in the same measuring that finds its
  programs, the room on the filesystem its copies are kept on and, for each leg the command
  builds, what that leg's build directory - and every other copy's of the same variant - holds as
  recorded, with the room where each is. The room is the filesystem's own count.
- **Legs sharing a filesystem add up.** Legs building on one filesystem of one host are counted
  together, in the order they were selected, because every build directory stays once built. A
  leg that does not fit beside the ones before it is `skipped-unavailable`, naming what is free,
  what it needs and why, and what the legs before it need; they are kept.
- **Unknown is not refused.** A leg nothing has measured that declares no `buildSpaceGiB` is
  placed as it always was, and so is one whose directory no build of this version recorded,
  since what that directory holds is an amount nothing measured.
- **Only a leg its command builds.** `sync`, `clean` and a run's leg it does not build need no
  room: clean is how room is made. A run builds a leg as its runner, its steps and its run checks
  need, on that leg's system: a step limited by `runOn` asks only about the legs of its systems.
- **A sweep fills its first worker.** A sweep of a leg's mutation arms builds in workers kept in
  its tree, never in the leg's own build directory, and is placed by the room its first worker's
  build still needs - the least it runs with; each worker is then measured as it is planned. A
  self-test builds no tree of the leg's at all, and no host is asked about its room.
- **A WSL distribution's disk grows on this machine's drive.** WSL 2 keeps a distribution's
  filesystem in a virtual disk file, and what the distribution measures is that disk's own room -
  a terabyte by default - whatever the drive holding it has left: measured, a distribution said
  814 GiB free while its drive had 563 GiB. So the room is also measured on the drive WSL
  registers as holding the disk, a WSL leg needs its room on both, and the drive is counted with
  this machine's own legs on it, which fill the same room.

`legs -v` says the room on each host it measured - where its copies are kept, and the main
checkout for this machine, with the drive a WSL distribution's disk grows on - or why it could not
be measured, so a host that is nearly full shows before a run fills it. `--json` always carries
it, as each host's `space` and a WSL distribution's `diskImageSpace`.

## Syncing a tree

`sync` puts a host's copy of the repository in step with this tree. It is the same code path
for this machine, a WSL distribution and an ssh host, so a sync to a host and a sync to a
directory here cannot drift apart.

- **A copy is made of what was read.** The tree is read once - its configuration, what it
  withholds, and every file it carries, by size and hash - and a sync decides everything by that
  reading: the `sync` command reads it once for all its hosts, and a run once as it begins, so no
  two copies are two moments of a tree that moved in between. Each file carried is checked against
  the reading as it is read for carrying, and the first that changed or went since stops the sync,
  naming it, rather than reaching the copy as content no reading recorded. A stopped sync is never
  indexed, given its configuration, verified, marked complete or marked adopted: what it carried
  before stopping stays on the host. `sync` fails, exit 20, saying so, and a run's legs on that copy
  are `inputs-moved`.
- **A reading confirms itself.** One pass hashes each file at its own moment, so a write that
  begins and ends while the tree is read gave a reading holding some files as they were before it
  and some as after - and a warm copy, which already held the earlier files, was carried only the
  later ones, and verified against a tree that never existed. So the reading notes how each file
  stands - its length, and when it was last written and created - before hashing it, and once every
  file is hashed it lists the tree again and looks at each file, reading no byte: the same files,
  each standing as it did, or the reading is refused, `inputs-moved`, `'<tree>' changed while it was
  read: '<path>' was written` (or `appeared`, or `went`) `. Let it settle, then run again.` A
  reading that passes is the tree as it stood the instant the hashing ended. The times are
  compared for equality alone; what the second look cannot see is a rewrite of the same length
  that puts the old times back. A host reading its own copy's manifest makes no second look: that
  copy holds still under the sync that asks.
- **A finished copy records which reading it holds.** The reading says the commit the tree's HEAD
  named and how many of the paths it carries git calls changed; the marker a finished sync writes
  records both, and the run that read the tree, as `commit`, `uncommittedChanges` and `readBy`, the
  names a leg's `tree` gives them - also where nothing else of the copy changed, since what it holds
  is then that run's reading. A copy a sync begins writing records none. A run's sync releases the
  copy before its legs take it, and another command's sync can run in between, so each leg asks the
  copy, under its own lock, whose reading it holds: another's ends the leg `inputs-moved`, `'<copy>'
  on <host> was synced again by run <id> after this run made it the tree it read, so it no longer
  holds that tree.`, with nothing of it run; and so does a copy gone, part made or unmarked by then,
  `'<copy>' on <host> no longer holds the tree this run read into it: it is not there, or no sync of
  this tool finished it.` - or, for one part made, what its marker says of it. A build from before
  this one refuses a marker carrying a reading as unreadable, as it refuses any member it does not
  know, so it reads no copy this one finished. Only going back to such a build meets that - a host
  is brought to the build of the machine that asks it, and one ahead of that machine is refused -
  and the remedy is to update again, never to delete the marker, which would leave the copy one to
  take over.
  The run says each reading as it is made - `'<tree>' was read at commit <12> with 2 uncommitted
  change(s)`, or `with no commit yet` - and each leg on a copy says which it tested with its
  verdict, `tree read at <12>`, or under `--use-staged` what the copy records, `tree staged at
  <12> by run <id>`. In `--json` each leg's `tree` holds `path`, the tree it worked in as its
  machine spells it, and for a copy `copyOf` and, where known, `commit`, `uncommittedChanges` and
  `readBy`: a leg in its tree has `path` alone.
- **A copy a sync is writing is marked unfinished**, before its first write or deletion - or its
  configuration, where that changes - and complete once it is verified, as a takeover is marked
  begun and finished. A sync that stops part way - a tree that moved, a connection that dropped, a
  verification that failed - leaves part of one tree and part of another, which no run began with.
  Marked so, the legs of a run with `--use-staged` on that copy are `inputs-moved`, read by the
  machine that would have synced it - as are those on a copy that is not there, or that no sync of
  this tool marked - and `sync --artifact` carries nothing into it, until a sync
  finishes it. The next ordinary sync, which plans from what the copy holds, puts it right; marking
  a copy again keeps when, and by which machine, it was made. The mark is written into the marker
  only while it holds, so a finished copy's marker never says it; a build from before the mark
  refuses an unfinished one as unreadable, with the same remedy.
- **The copy is the tool's.** Sync creates it, records that it did, and refuses to write into a
  directory it did not create. It deletes whatever the source does not have, so taking over a
  checkout somebody made by hand could delete work nothing here knows about, on a machine whose
  owner is not watching.
- **The refusal says what taking it over would cost.** It is worked out from the same manifest
  and plan a real sync uses, so the reader is told which files would be overwritten and which
  deleted, rather than only that the directory is not the tool's. An overwrite is named apart
  from a write everywhere it is reported — in the refusal, in `--dry-run`, and while it happens —
  because a file the copy already had, holding an edit nobody committed, reads exactly like a file
  the copy never had, and only one of the two loses anything. For the same reason an overwrite is
  reported by default rather than only under `--verbose`, as a deletion already was.
- **`--adopt` names the hosts it takes over** — `--adopt vps`, not a bare yes. One sync reaches
  every host, so a flag meaning "go ahead" would take over whatever unexpected directory is found
  at another host's `repositoryPath`, a mistyped one included, without being asked again. A host
  nobody named is refused exactly as it would have been without the flag, and told which spelling
  would take it. A directory on this machine - a leg's copy - is never taken over: no command
  that reaches one takes an adopt list for it, so its refusal names what a person can do, to move
  it away or run the leg with `--in-place`.
- **It says what that is about to cost before it starts** — not only in the refusal, because
  somebody who reads the flag in the help and types it never sees a refusal.
- **The marker records which it was.** Afterwards a copy taken over and one the tool made are the
  same directory, and only one of them deleted somebody's files; the marker is the only thing left
  that can say so.
- **What survives an adoption is narrower than it looks.** Its `.git` and so every commit, branch
  and tag in it, its `.orchestrators`, `.mutations` and `.legs`, the rest of `.harness-config` save
  the runner's actions, the worktrees root and whatever `sync.neverTransfer` names are protected
  from the deletion — though `config.json` and the actions there are replaced with this tree's,
  and its git HEAD is moved, detached, to this tree's commit where it names another, both of which the refusal says. The ignore list is *not* read from that host: it is this tree's, listed by asking
  git which ignored files exist **here**. A directory only the host has — a build tree under a name
  `sync.neverTransfer` does not carry, a `node_modules`, a virtual environment — is ignored by
  nothing this side can see and is deleted like any other file. It appears in the list the refusal
  prints, which is why the list is the thing to read; name it in `sync.neverTransfer` first if it
  should stay.
- **`sync.maxDeleteFraction` bounds an adoption too.** A directory that exists and carries no marker
  is exactly what a mistyped `repositoryPath` produces, which is the case that bound was written
  for: the path meant to name a checkout names a home directory, and every other project under it
  is what the source does not have. Two gates for that is the point of having one.
- **A takeover is marked as begun before anything is deleted, and as finished only once the copy is
  one.** Both of the obvious markings are wrong about a run that stopped part way: unmarked, the
  next run refuses and reports a smaller loss than the first did, because what has gone no longer
  appears in a plan; marked complete, the next ordinary `build` or `test` — which never carries an
  adopt list — would quietly delete the rest with nobody asked at all. So a stopped takeover is its
  own state: it still needs `--adopt <host>`, and its refusal says plainly that what the earlier run
  removed is not in the list.
- **A link the copy holds is named too.** A link sits in no manifest — the walk refuses to follow
  one — so a file written at its name replaces it and reads as an ordinary write, and everything
  behind a linked directory is outside any list a plan can build. Reported rather than guarded
  against: a build directory pointed at another volume is ordinary, and refusing to write through
  one would refuse `--pull build/...` — a path the reader named, inside the tree they named.
- **`--dry-run` shows the plan, and exits as the sync would.** For a directory the tool did not
  create, for one whose deletions are over the bound, and for a worktree's copy another worktree
  of the same name still holds, the dry run lists what the sync would do - the bound's own message
  says to run with `--dry-run` to see the list - and exits 13 with each refusal the sync would
  make, in its own words: both, for a directory not made here that is over the bound too. Who holds
  a worktree's copy is read, and nothing recorded. A dry run is the answer to "would this go
  through": a code of 0 says only that nothing known at that moment stops it, as every dry run's
  does (see *What a dry run answers*).
- **The copy is created, with its parents,** when the declared `repositoryPath` is not there, so
  the first sync to a fresh host needs no hand-made clone. A path that exists and is not a
  directory, or that cannot be created, is a named failure — never a silent fallback to
  somewhere else, because a leg would then report on a tree the reader cannot find.
- **The copy is a git repository,** because the DssHarness on that host finds everything through
  git. It is made one after the transfer, so a copy that failed part way is never left looking
  complete.
- **The copy's index holds what the sync carried,** and nothing else: every file of the transfer and
  the configuration placed with it, staged as they stand, and anything the index held that the sync
  did not carry removed - on every sync, so a copy made before this is put right by the next one,
  whatever that carries. Everything on the host that reads "the files git tracks" reads the index: a
  build's input fingerprint, which lets the next build keep its directory, and the guards that hold
  a build's or a step's inputs still. Written without staging one, a copy's index named nothing, so
  every build there after the first started from clean and every such guard watched nothing,
  without a word. A file written through a link in the copy is outside it and is not staged; the
  write is warned of, and the verification refuses the copy. A tree git tracks nothing in is now
  said, by a build and by a step that asked for its inputs held still.
- **The copy's HEAD names the commit the tree was at** when it was read (`SyncService.CarryHistoryAsync`),
  so what a step asks git about HEAD - which commit, what a file held there - is answered in the
  copy as it is in the tree. It never was: a copy this tool made named no commit, and a clone it
  took over named whichever its owner left it at, so a consumer's runner, whose program reads
  `git rev-parse HEAD` and `git show HEAD:<path>`, passed its local leg and failed its host leg
  over files that were byte for byte the same, and nothing said why. The commit is read with the
  tree (`SyncSource.History`), so every copy of one reading names one commit, whatever is committed
  meanwhile. The question rides on the request a sync opens with (`inspect` answers, with whose
  the directory is, where its repository's HEAD stands and what it holds of the commit asked for),
  so it is weighed before anything is written there, and a sync that finds the copy's HEAD naming
  the commit, and holding what is asked, costs no request more and sends nothing. Otherwise this
  machine packs what the copy lacks (`git pack-objects --revs`), sends it a piece to a request,
  none larger than a batch of files (`history-piece`, kept in the copy's git directory under
  `dssharness-incoming`), and the copy takes it (`take-history`, `GitClient.TakeHistoryAsync`):
  `git index-pack` reads the pack whole where it was kept, checking each object, and only then is it
  moved among the repository's own, its index first - git's own taking of a fetch writes the pack
  a second time, into a temporary file among the repository's packs, which a take stopped part way
  would leave there, the size of the pack, for nothing to clear. Then every object the commit
  names is walked (`git rev-list --objects`), and only then is HEAD moved - itself, detached
  (`git update-ref --no-deref`), never the branch it was on, and not at all where it names the
  commit already. A pack that is none, or one the copy is then found short of, leaves HEAD where
  it was, and the sync fails with what git said of it. A sync with only a HEAD to move marks the
  copy unfinished until it has, as one with files to write does, and a dry run says what it would
  do to HEAD and does none of it.
  - *How much.* `sync.history` is `head` by default: the commit and what its tree names, packed as git
    packs a clone of depth one (`--shallow <commit>`), less what the tree of the copy's own HEAD already
    gives it (`^<its HEAD>^{tree}`). A question about a commit behind it that the copy does not
    hold fails in the copy, in git's own words. `full` packs every commit behind it too, less those
    behind the copy's own HEAD (`^<its HEAD>`), where the copy holds them all: a copy given the
    commit alone before is sent all of it, the commit included, once. A tree whose own repository is
    a shallow clone gives a copy all it holds and no more - the commits its history stops at go
    with the question, so the copy is not read as still short of them, and sent the whole again
    with every sync. A partial clone asked for `full` has git fetch, from its remote, what it left
    out, or fail where it cannot. In the walk of a `full` take, what the copy's own HEAD, branches
    and tags reach is trusted to be whole, as git trusts it in a fetch.
  - *One pack to a reading.* Every copy made from one reading that lacks the same of its history is
    sent the same pack, made once (`HistoryPacks`, the `SyncSource`'s own, removed with it) - a
    sweep's workers were otherwise each packed the same commit. It is kept in the tree's own git
    directory, under `dssharness-history-<id>`: on the volume that already holds the repository,
    never a temporary directory a system may keep in memory, and there before the room a sweep's
    workers need is measured. Each such directory is recorded as its process's own in
    `<directory>.owner.json` beside it, written first, and the next reading that packs removes one
    whose process no longer runs - what a sync that was killed left, which nothing else would
    clear. One recorded by another machine cannot be asked, and stands.
  - *Where history stops.* git reads a commit listed in the repository's `shallow` file as one with no
    parent, and one not listed with a parent missing as damage. After each take the list is made
    exactly true of the commits it could have changed for - those listed before, the commit taken,
    and those the tree's own history stops at: each the copy holds, a parent of which it does not, is
    listed, and no other. So a commit whose parents arrived stops being a boundary, and history that
    arrived a commit at a time reads as far back as it goes. Where the commit alone was asked for
    and the history behind it cannot be walked - a commit an earlier take that stopped left without
    its parents, recorded nowhere - the commit taken is listed too. Written under the lock git
    takes, never past one that is there, and left as written where the walk then fails: it is true
    of what the repository holds, whether or not that is all the commit names.
  - *A copy taken over.* Its HEAD is moved like any copy's, where it names another commit, and no
    branch, tag or commit of its repository changes: the branch it was on names the commit it
    named. What does change is said. The repository gains the objects sent; its index is the
    sync's, as any copy's is; and one that held every commit behind its own, given a commit
    without the one before it, is a shallow repository to git from then on - a fetch there brings
    nothing from behind that commit until `git fetch --unshallow` is run there. The takeover's list
    says HEAD will move, and that this can follow, before either happens; the sync says, the once
    it happens, which branch HEAD left and where it was, and that the repository is now a shallow
    one; `git reflog` there has the move too. After that its HEAD is on no branch, and moving it
    moves nothing of anybody's.
  - *What is still the copy's own.* Its branches, remotes, tags and stash, and a submodule's
    history, which is not carried. What changed since HEAD is answered for the files the copy
    holds, each by its bytes: a file this tree tracks and the sync withholds reads there as
    deleted, one it carries untracked as added, and one git stores converted on this machine - line
    endings (`core.autocrlf`), a clean filter - as changed. A tree whose HEAD names no commit yet
    gives a copy none, and the sync says so where the copy's HEAD names one. A copy whose
    repository names its objects another way than the tree's (`sha1`, `sha256`) can hold no commit
    of the tree's: the sync is refused before anything is written there, naming both, and a copy
    made for a tree names them as the tree does. An object's name is taken from another machine
    only whole - 40 or 64 lowercase hexadecimal digits (`GitObjectId.Require`) - before it reaches
    a command line or a file's name.
- **Content, never timestamps.** A file is written only when its content differs. An unchanged
  file is not touched, so its modification time does not move and an incremental build on that
  host stays correct; a changed file is rewritten now, so its time advances. Nothing compares two
  times taken at different moments or on different machines.
- **Deletions propagate.** A file removed from the source is removed from the copy in the same
  sync. A copy that only ever gains files is not a copy of the tree: a deleted source file keeps
  compiling, a deleted test keeps running, and a renamed file exists twice, so the leg's verdict
  describes a tree that no longer exists.
- **The never-transfer floor is also a never-delete floor.** `.git`, `.orchestrators`,
  `.mutations`, `.legs`, the worktrees root, everything under `sync.neverTransfer`, and all of
  `.harness-config` but `config.json` and the runner's actions are neither written nor deleted. A
  path the harness will not write is one it cannot know the source lacks, so deleting it would
  remove the host's own state rather than a file the source gave up — including the build
  directories that make an incremental build possible. `sync.exclude` is different: the source
  chooses not to send those, and a copy that kept them for ever would be a copy of a tree that no
  longer exists, so they are deleted.
- **What git ignores is never transferred, and never deleted.** Asked of git once per sync, so a
  local `.env`, a virtual environment or an editor's cache never reaches a host, and the host's own
  copies of such things are left alone. `sync.exclude` names paths to withhold *in addition* to
  these.
- **A `sync.neverTransfer` name that protects nothing is named.** An entry is rooted: `.secrets`
  covers the root's and no other, so one absent from the root while the name exists deeper protects
  nothing, and a reader takes it for protection. Before every sync the tree is searched for such
  names, and each is named with the fix, `**/<name>`. A name counts only where nothing the
  configuration writes covers it - not where an entry covers the path, the `**/<name>` the warning
  asks for among them, and not in the worktrees root - or the author who followed the advice would
  be told it again on every sync. The search goes where a sync goes, and into the harness's own
  directory besides: never into what a sync withholds - what an entry covers, the worktrees root,
  what git ignores, what `sync.exclude` names - though such a directory's own name is seen from the
  one holding it, so a `node_modules` git ignores still counts. What lies inside is generated,
  fetched or another checkout's, and it is where a tree's size is: on a consumer's tree 68,697 of
  69,890 directories lay under what its entries name, and a search that went in spent its
  20,000-directory budget before it reached most of the tree, and said so on every sync. The
  harness's own directory is searched all the same, though git ignores most of it by design,
  because a `.secrets` there is what the search was written to find - save what the harness writes
  there as it works, each run's records, the record of host copies, the record of mutation verdicts,
  the dependency sources a mutation worker is given and each action's own `build` and `artifacts`: the run state of whichever machine made it, which no
  sync carries and no one edits by hand, where a name neither counts nor is looked for below. Counted, a fresh worktree's
  first run, its own `build` not made yet, was told the `build` entry `init` writes protected
  nothing, having found an action's working space, and to write `**/build`, which would withhold
  every source directory of that name too. A search that still runs out says so; it never reads as
  having found none.
- **The copy gets `.harness-config/config.json` and the runner's actions, and nothing else from
  that directory.** A leg placed on a host runs DssHarness there, and DssHarness in a directory
  holding no configuration refuses as not initialised — so without it the copy is a tree no leg can
  run in. The actions cross as any file of the tree does - never what git ignores, never an
  action's own `build` or `artifacts` - since a leg there reads its runner's action file from the
  copy. The rest of the directory is connection data, credentials, runner values, locks and run
  state, each local to a machine by design.
- **Deletion is bounded.** A sync that would delete more than `sync.maxDeleteFraction` of the
  copy stops and changes nothing. A mistyped repository path makes the source look empty, which
  is indistinguishable from a source that deleted everything, and without the bound that empties
  a host. A first sync into an empty copy has nothing to measure and is never over it.
- **Deletion stays inside the tree.** Every path is resolved against the copy's declared root and
  refused if it leaves by `..` or by being absolute. Links are compared as spelled rather than
  followed, for the reason the takeover bullet above gives, and are disclosed instead.
- **What was deleted is reported** by name, at the level a reader sees by default rather than
  behind `--verbose`: what a sync removed from another machine is the one thing running it again
  cannot recover. `--dry-run` lists every write and every deletion and changes nothing.
- **The result is verified, not assumed.** After the transfer the copy's manifest is read back and
  compared with the reading of the tree the sync was made from. A tree that still differs fails,
  names what differs, and says nothing should be run against it.
- **What crosses is never held as text.** Files cross to a host in batches of up to 8 MiB or 512
  files, a request each, written onto the host's standard input as it is encoded: each file's path,
  then its bytes as base64, a piece at a time. A file read back - what `--pull` brings, and what
  carrying an artefact reads to prove it landed - follows its answer a piece to a line and is
  decoded as it arrives. Each value an operation takes is an argument of its own after `--`, so a
  path starting with `@` or `-` is that path on the host, never a file of arguments or an option: a
  file named `@notes` had its deletion read the file `notes` beside the copy and delete whatever
  path that named. A consumer's first sync of a worktree of 85 MiB built each batch as text inside
  text, and the serializer's buffers for it - six times a batch's length, rented to escape it - were
  kept by the shared pool for the life of the process: the machine that carried it held 3.2 GiB,
  flat, while the leg ran for minutes after. Carried now, the same tree leaves it about 40 MiB, and
  reading back a 64 MiB file 90 MiB where it left 2 GiB. A file crosses to a host whole, inside one
  request, so the largest that can is one whose base64 text, with the rest of the request around it,
  a host reads as one line, and one string holds: 804,519,909 bytes; one read back is held to the
  same. A larger one is refused by name before anything is sent.
- **Staging is the two commands, not a flag.** `sync` transfers and stops — that is all it ever
  does — and `build`, `test`, `run` and `check-mutations` take `--use-staged` to act on what is already there without
  syncing again. A `--stage-only` on `sync` would name a mode `sync` is always in.
- **Artefacts come home** with `--pull`, each file hashed on the far side and checked again on
  arrival. Evidence that a binary built here runs there is not evidence if nobody checked it
  survived the journey. A leg's line in `run --json` names each file its steps kept as
  `keptOutputs`, relative to the tree, so a caller passes those paths to `--pull` without walking
  the host's tree for them. A path ending with `/` names a directory instead: the host that holds it
  lists every file below it with its size (`ISyncTransport.ListFilesAsync`, served as `sync-serve
  list`), never following a link below it, and each file then crosses as a file named does, in a
  request of its own. The side that holds the directory bounds it before a file of it is read - 256
  files, 1 GiB together, no file past what one request carries - and one that is not there, is a
  file, or holds no file fails the pull by name (`SyncService.ListedForPullAsync`), every directory
  before any file has crossed. A directory that cannot be read where it is fails the pull by name
  too, never as a defect of the tool. It is asked of the host with forward separators the whole way
  along, however it was typed: a shell on Windows completes each part with a backslash, which a
  host that is not Windows read as one name. Each file brought back is named in the report, and
  each link passed over; a dry run asks the host and names the same files. A pull that stops part
  way - a file gone since it was listed, one that did not land intact, a host that stopped
  answering, an interruption - ends there (`SyncPull.Stopped`) and fails as what stopped it does,
  naming each file it had brought back, which stays where it was written, and each it had not:
  raised as the one failure it stopped at, a directory here held part of what the host kept, and
  nothing said which part.

## Predefined runners

A procedure specific to one repository — a corpus build-and-test, a benchmark, a round trip —
lives in `predefinedRunners` rather than in the tool. `run <name>` executes one across the legs
it declares, with the same isolation, locking, stall bounds, witnesses and reporting every other
leg-running command gets. A runner that declares `requireBuild` has its leg built before it runs:
a runner that calls a program the build produces otherwise runs against whatever was left there.

**A step naming what the build makes builds its leg first, whichever runner starts it.** A run that
runs a step whose run line or `workingDirectory` names `{product}`, `{buildDir}`, `{compiler_C}` or
`{compiler_CXX}` - however the run comes to run it: by default, named with `--manual-step` or under
a runner's `steps`, or needed by one that is, through a runner that declares `requireBuild` or not -
builds each leg before its steps start; one limited by `runOn` builds the legs of those systems
alone, and a runner's own phase naming one builds as a step does. Read from the line rather than declared
beside it, because a line naming the product reads the product whoever starts it: when the build
was declared only on runners, a manual step naming `{product}`, kept in an action a runner that
builds nothing also runs, was started through that runner and read whatever the last build left -
a file that was not there, and the leg passed. A runner whose expected exceptions' run checks name
a runner that needs the build builds first as well: a check runs on the leg as the runner carrying
it left it, and is never built itself, since rebuilding mid-run would replace what the failure it
explains came from. A leg that builds asks its host for the build's programs and room, and is heavy.

A leg a run would build that cannot be built - it names no project, or no toolchain for its
system - is refused before any host is measured, naming the leg and what builds it, and so is a
step or phase of the run's own runner naming `{product}` on a leg whose project declares no one
file for its system, or naming a compiler on a leg whose project CMake does not build; one refusal
names every such leg, of each kind. For a runner requiring the build, the first was found only
where the leg's build began: it ended the whole run once its hosts were measured and its slots
taken, without saying what built the leg; and the second was refused only after the build it had
just cost, as the third would be. A product or a compiler a run check's step names is refused by
the check that runs it, when it runs: refused before the run, it would refuse a run whose checks
may never run. The build a check needs is made before the run whether or not the check runs, so a
leg it cannot build is refused then.

**A leg's own compiler is named, never typed.** `{compiler_C}` and `{compiler_CXX}` are filled in
with the program the leg's build identified for that language: the whole path CMake's record of
identifying it names, in the leg's build directory, as that machine spells a path. It is the
record every configure of the directory after the first loads, and the one the build's own
`compilerId` witness is held to - so the name is what built the leg, and never what a toolchain
declares, which may be a bare `gcc` that `PATH` resolves differently for the next process, or
nothing at all where CMake chose. A runner that must judge each leg through that leg's compiler
says so in one run line, which is gcc on one leg and cl on another; an `--input` reaches every leg
of a run alike and could not. A line may start with one, written alone: the tool policy allows the
leg's own compiler as it allows a declared tool, since the build ran it. Anything beside the name
makes the line another program, judged as what it starts; so does a path that merely equals the
compiler's, from an input or a value, which nothing says a build identified.

The name is known only once the leg is built, so a step naming one builds its leg first, and what
fills it in is read then, once, for the check before the first step and the step itself. A name
nothing fills in is refused before the first step runs, saying which case it is: no compiler
identified for that language, a record that could not be read or that names no program, or a
compiler the build runs with words after it - a launcher such as ccache given its compiler, a compiler given options. The name
is a program alone, and the program alone is not what built the leg, so the refusal names the
program and the words and hands over neither. Which legs have a compiler at all is the
configuration's to say - only a build CMake configures identifies one - so that is refused before
any host is measured, and never after a build it would have cost.

A test invocation names one too, in its `args`, `coresArgs` and `workingDirectory`. Its command is
checked before the leg's build, where no compiler is identified yet: there the name stands as
written, and is filled in - or refused, as above - once the leg is built and before its runner
starts. Under `test --no-build` it is filled in from what the build already in the leg's directory
identified, which is what built the binaries it tests. A sweep of mutation arms fills it in from each worker's own build, which is what the
worker's tests run against.

**A runner is refused on a leg the run did not build, where what it runs reads the build.** The run
decides the build, and says to each runner it starts - its own and each a run check starts - whether
it built the leg; a runner requiring the build, or a step or phase naming `{product}`,
`{buildDir}` or a compiler among those that leg runs, is refused there before anything changes the
tree, naming each. Nothing an earlier build left there is read to fill a compiler's name in
meanwhile - a runner's; `test --no-build` tests what an earlier build left, and reads its compiler
with it. It is the rule above asked again of what will run, so a file edited between the run's reading
it and the leg's is refused rather than started on whatever the last build left.

**`cleanDirectories` never reaches what the run stands on.** Each is deleted, with all it holds,
before the runner's first step - after the build a run makes first - so one that is the whole tree,
lies outside it, or is, holds or is inside `build` (where every leg's build is kept, and stays
incremental), `.harness-config`, `.git`, what orchestrators keep or the worktrees root is refused
when the file is read, compared ignoring case. So are a phase with a blank name and two phases of
one runner sharing a name: each writes the log its name names, and a resumed run skips the ones its
name says were done. Two steps a leg runs whose names differ and are kept as one log are refused
before any runs, naming both and the log: a character a file name cannot carry is kept as `-`, so
`a/b` and `a:b` are one file's, as are two names differing only in case.

**`requireBuild` gates the build, never the sync.** A leg on an ssh host or a WSL distribution runs
from that host's own copy of the tree — the host reads `config.json` and the runner's action file
from it — so the tree is put there whether or not anything is compiled. A runner that skipped the
sync because it compiles nothing would find no configuration on the host and fail saying so.
`--use-staged` is how a run says the copy there is already current - which it is not after a sync
that stopped part way, or failed its verification; a copy so marked makes its legs `inputs-moved`,
as does one that is not there, or that no sync of this tool marked.

Runners are keyed by name because a name is how one is selected — by `run`, and by the checks
below. An unnamed entry in a list could not be selected at all.

### Action files

A runner declares either `phases` or an `action` naming a YAML file under
`.harness-config/runner/actions`, never both: two descriptions of what one runner does would
eventually disagree, and nothing could say which one ran. Every field a phase carries —
`workingDirectory`, `env`, `successPattern`, `stallSeconds`, `continueOnError` — is a key on a
step, so nothing the verdict contract depends on is lost by declaring one instead of the other.

The reverse does not hold. A key a step takes and a phase does not — `uses`, `runOn`,
`workingDirectoryRoot`, `outputs` and `persist` among them, all listed by `help runners` — belongs
to a step, and `config.json` refuses it on a phase, naming the key and pointing to an action file.
`outputs` are checked, and kept with `persist`, in the action's own `build` and `artifacts`, which a
runner of phases does not have: accepted on a phase, they were a check nobody made, and the phase
passed without it. A phase does take `stepName`, the step it is reported under in `ranSteps`.

**Every key is listed.** `help runners` lists each key a runner, a phase, an action file, an input
and a step take, with what it does, read from what the files are read with: a runner's from the
contract `config.json` is read with, each meaning beside the member that reads it, and an action
file's from the one list its parser reads, which refuses a key missing from it before looking at
it. A key cannot be taken without being listed.

**One directory per action.** An action lives at `actions/<name>/<name>.yml`, and everything its
steps run — a program, a fixture, a data table — lives in that same directory. A `run` line is a
program and its arguments with no shell, so anything that is not a one-liner has to be a file;
a flat directory gives that file nowhere to live that is obviously owned by the action it belongs
to, and two actions' supporting files would sit side by side with nothing saying which was whose.
The file carries its directory's name so that neither can be renamed quietly into disagreeing.

The rule is applied in two places on purpose. Its **spelling** — two segments, no `.` or `..`, not
rooted, ending in `.yml` or `.yaml`, the file named for its directory — needs no file system, so
`config.json` is refused for it when it is read, and `legs` and `run` therefore answer the same way
about the same repository. Its **resolution** — that the file is there, and that no link along the
path leads out of the actions directory once every link is followed — is what only the file system
knows; `legs` and `run` both check it before a leg is placed, and the parser checks it again when
it opens the file, because the last line of defence does not get to assume a caller validated
first. Containment is compared with this platform's path rules and at a directory boundary, so a
sibling directory whose name merely starts the same way is outside, not inside.

- A step either `uses` a predefined action or carries a `run` block. There are two predefined
  actions, confirming the tree is at a named commit and reading inputs; an unknown one is refused
  naming what is available.
- A `run` block is split on newlines and each line is trimmed, so indentation and blank lines
  cannot change what runs.
- **A step runs at the leg's tree root unless it says otherwise.** That is measured behaviour and
  it did not change with the layout above, which reads as though a step ran beside its own file.
  `workingDirectoryRoot` names what `workingDirectory` starts from — `tree` (the leg's worktree or
  the repository, the default), `harness` (`.harness-config`), or `action` (the action's own
  directory) — and `workingDirectory` is a path under it, the root itself when absent. A step that
  runs a program it ships says `workingDirectoryRoot: action` and names it `./probe.py`; a step
  that builds or tests the repository says nothing and keeps the root it always had. The roots are
  resolved relative to the leg's own tree, so a leg on a worktree reaches that worktree's copy.
- **`runOn` picks a step per operating system.** A step naming `runOn: [windows]` runs only on a
  Windows leg; one without it runs on every leg. A leg of another system drops the step before the
  file is vetted, its names demanded or its phases made, so nothing about the step is asked of that
  leg - neither its program, which no survey requires of that leg's host, nor a name its lines use.
  The leg says so as it runs, and lists the step as `skippedSteps` on its line in `--json`, so a
  step left out is never simply absent. A step that reads what a skipped step would have made finds
  nothing there; the two take the same `runOn`. A run in which some leg's system runs no step at
  all is refused before any host is measured, naming every such leg: it would pass having run
  nothing.
- **A step can run only where a run names it.** `manual: true` keeps a step out of a run that
  names no step, for work that belongs with an action and is not part of what running it means - a
  benchmark sharing modules with the build and test beside it, which one action per directory would
  otherwise leave nowhere to live. `run <runner> --manual-step <step>` runs only the manual steps it
  names; a runner may name its own steps in `config.json` (`"steps": [...]`), manual or not, and
  becomes a runner with legs of its own that a gate names like any other; the command line wins over
  the runner. Whichever chose them, the steps chosen are the file from then on: selection runs
  before `runOn`'s, and the tool policy, the names a step may use, the inputs a run may be given,
  the programs a host is asked for, whether a leg is built first and the refusal of a leg that
  would run nothing all read only what will run. A step lists under `needs` the steps declared
  before it that run first whenever it does - one declared after it could not have run by then,
  one that does not run on every system the step runs on would leave a leg there running the step
  without it, and a step every run runs
  needing a manual one would make a plain run run it, so each is refused when the file is read.
  Steps of one name are one step to whatever names them, narrowed by a leg's system to the one it
  runs, so a manual step cannot share its name with one that is not. A manual step declares a
  `successPattern`, since it is the step whose green line is read as having done the work it was
  named for; a predefined action cannot be manual. A run says of every step it did not select that
  it did not run it, as it goes and as `unselectedSteps` on each leg's line in `--json`, and lists
  the steps a leg ran as `ranSteps` and the manual ones among them as `manualSteps`, so a plain run
  is never read as having benchmarked. A `--manual-step` naming a step the action lacks or one that
  is not manual, a runner naming a step its action lacks, and a leg on whose system none of the
  steps a run names runs - it would run only what they need, and pass, with the step named run
  nowhere - are refused before any host is measured, naming the steps there are. Whichever runner
  starts it, a step that names `{product}`, `{buildDir}` or a compiler has its leg built first.
- **An input's value comes from `run --input name=value` first**, the runner's `.env` directory
  second and the input's own `default` last; `.secrets` never gives an input a value. A step may
  declare inputs of its own beside the action's, resolved the same way and read by that step
  alone: another step naming one names nothing, and a name the action already declares is
  refused, since one value could not mean both.
  `--input` takes one pair each time it is given, for an input the action declares or a step the
  run runs declares, and only for the runner the command line names - a runner a run check
  starts reads its own values. Any other name, a runner of phases, an empty value and a name given
  twice are refused before a host is measured: an unset shell variable is not a request to run
  with nothing, and a value for a name the file never reads changes nothing while the command line
  says it did. A host running one of the run's legs is handed the same pairs, and the same
  `--manual-step`s, so no leg there runs a default where the command line gave a value, or the
  runner's own steps where it named others. The value is a plain one, on a command line and so in
  the process table; a secret stays in `.secrets`.
- **Each line is a program and its arguments, never a shell string.** No shell parses it, so no
  shell's word splitting, globbing or process emulation sits between the harness and the program.
- The splitter honours double quotes only, understands no escape, and strips every `"` from the
  token it returns. Measured: an **unterminated** double quote makes it drop the rest of the line
  silently. A line with unbalanced quotes is therefore refused when the file is read, because the
  alternative is a command that runs with arguments nobody can see are missing. An argument that
  must keep a quote, or that uses single quotes, is carried as its own list item rather than
  inside a `run` line.
- The first token must be a program the configuration declares under `tools`, a path inside
  the repository, or the name of one of the leg's compilers alone - `{compiler_C}` or
  `{compiler_CXX}`, which starts what the leg's build identified and built with. An undeclared
  program is refused before anything runs — the same list `install-missing-tools` guarantees is
  installed. It is judged as it will start: its names
  filled in, and a relative path read from the directory its step runs in, both worked out by the
  one function the run starts it with. Read as written, `{dir}/tool` was inside the repository
  while the step started a program wherever `{dir}` pointed. A refusal says what a line was
  read as where the file does not already say it, masked where a secret filled it in.
- A line that fills in to nothing starts nothing, and is refused naming it; so is a step whose
  directory fills in to nothing, or whose names hold a character no path can. A runner's own
  steps are held to the same rule before the first one runs.
- **Every name in braces a step writes is filled in, or refused, before the first step runs**,
  arguments included: `{product}` on a leg without exactly one was refused only when its own step
  began, after the ones before it had run. A name starts with a letter or `_` and holds only
  letters, digits, `_`, `-` and `.`; one the pattern cannot see reaches the program as its own
  text. So an input whose name a run line cannot write is refused where it is declared, and so
  are an input and a `.env` value named like one of the tool's own names: a run line naming one
  would get the tool's value while the environment held theirs.
- A declared input the run gave no value is refused as that, saying how it can have one:
  `--input` only for the runner the command line names, the `.env` directory the leg read, or a
  default - and for a secret, the environment. The guard against a secret in an argument list
  reads each argument as it will start, so a secret a name fills in is refused as one written out.
- Values come from `.harness-config/runner/.env` and `.harness-config/runner/.secrets`, each a
  directory of files, read by the one rule for a directory of dotenv files (`DotEnvDirectories`):
  every file merged, and a name defined twice - in two files, in one file, or in both directories,
  its case ignored - refused, as is a line that defines nothing, with its line. A value that came
  from `.secrets` never reaches a log, an argument list or an error message.
- An unknown key, at the top level or on a step, is an error, as it is in `config.json`: a
  silently ignored key is a rule nobody applied.
- A step that performs a predefined action runs no program, so a key only a run block reads —
  where it runs, its environment, its witness and bounds, its outputs, inputs of its own — is an
  error on it, for the same reason. `help runners` names them from the list the parser refuses
  them by.

### Expected exceptions

A runner may declare failures it is allowed to produce, each with the outcome to report instead
of an unexplained one. An entry has two halves that must not be confused: what it **matches** —
an exception type and a list of messages, each plain text or a regular expression, any one
matching being a match — and the **outcome** the match produces. A message is matched against
what the failure said and against each line the failing step printed, a line at a time as its
log keeps it, so one written across two lines matches nothing.

**An entry must show its work.** Every entry carries when it was earned, where, the mechanism
measured, and the anchor holding the evidence, and the file is refused without them. The lint
also refuses an entry that repeats another's type and messages, a message that does not compile,
and an entry that names no message or names one matching anything: that is an unconditional claim
spelled as a scope, and it excuses whatever happens to fail, hiding the regression it was written
to explain. Every measurement here is biased toward ABSENT — where the tool cannot establish that
an entry is scoped and earned, it refuses the entry rather than giving it the benefit of the
doubt.

An entry is scoped by the runner carrying it and the legs that runner declares, so an excusal
earned under emulation is never available to a native leg.

### The gate

An expected exception may carry `runChecks`, and until every one passes it excuses nothing.

- Each check invokes **another** predefined runner by name and compares its outcome with what the
  check expects. A field left out is not a requirement. `sameException` requires the same success,
  warning, result code and message as the entry it gates. An expected message is looked for in
  what the run said and in each line its passing steps printed, a line at a time, read from
  their logs.
- The runner a check names is never the one carrying it, and a runner reached that way may carry
  no checks of its own, so a check is one level deep and cannot recurse.
- **Unconfirmed, the failure stays genuine.** That is the whole point of the gate.
- A check that could not be run - refused, or its runner's inputs not watched - confirms
  nothing and says why: `could not be run (...), so nothing confirms the entry`. The run it
  gates was run, so its failure stays genuine, never `unmeasured`.
- **The window is the failing unit's own** — its output up to its verdict line — never a
  once-per-run sample. A failure is excused only when at least `minStepsInFailureWindow` steps of
  at least `minStepSeconds` fall inside that window. A once-per-run sample was measured charging
  genuine-looking failures to the tool under test on a loaded machine and excusing them on a quiet
  one, on the same day; a sample taken before a run says only what the machine was doing then.

## Set-ups

What a repository's work needs running beside it - a database, a container stack, a fake service -
is declared once under `setUp`, by name, as an `up` and a `down` command line:

```json
"setUp": {
  "e2e": {
    "up": "docker compose -f e2e/compose.yml up -d --wait",
    "down": { "windows": "scripts/e2e-down.cmd", "all": "./scripts/e2e-down.sh" },
    "shared": "machine"
  }
}
```

`up <name>` runs a set-up's `up` line at the root of the tree it is given, on this machine, and
`down <name>` its `down` line; each streams the line's output, exits `0` where the line exits `0`
and `20` otherwise, and under `--json` answers with the line, the values its inputs were given, its
exit code and how long it took. The name is matched ignoring case, and one `setUp` does not declare
is a usage error, `10`.

**A set-up's lines take inputs.** A set-up declares the values its lines name under `inputs`, each
with an optional `default` and a `description`; a line names one as `{name}` in any argument, and
`{{`/`}}` are braces. Each argument is filled in apart (`LegPathNames.Fill`, the one filler a leg's
paths use too), so a value holding a space is still the one argument it was written as. `--input
name=value` gives one on `up` and `down` (`CommandLineInputs`, the parser `run` reads its inputs
with), and what names the set-up gives them as `{ "name": "db", "with": { "profile": "ci" } }`,
each over the input's default. One reading of the values serves the file, the commands and a leg
(`SetUpValues.Resolve`): a value for an input the set-up does not declare, and an input left with
no value and no default, are refused - when the file is read for what names a set-up, and as a usage
error, `10`, for `--input` - and a brace no input fills is refused when the file is read. A set-up
is brought down with the values it was brought up with.

**A line is a program and its arguments, never a shell string**, split as an action file's run
line is (`CommandLineText`, the one splitter both read): double quotes keep a word whole, nothing
escapes, and a line with an unbalanced quote is refused when the file is read. A pipe, a
redirection, a variable or a wildcard is the program's argument as written, so what needs a shell
is a script the line names. Its first word is held to the run lines' rule (`ActionToolPolicy`): a
tool declared under `tools`, or a path inside the repository, a relative one read against the
tree's root, where the line runs - anything else is refused, `13`, before anything starts. Either
line may differ by platform, as a map of `windows`, `linux`, `macos` and `all` (`PerPlatformText`,
the shape `buildOutputs` takes too), `all` serving every platform the map does not name; a
platform it serves none of has no line, and the set-up is refused there, `13`, nothing run. A
program that cannot start is `14`.

**A run brings set-ups up only when asked.** A test invocation and a predefined runner name the
set-ups their work needs, under their own `setUp`; a platform's test section replaces the `all`
section's list, as its other keys do. `test --set-up` and `run --set-up` bring up, for each leg,
what its work names - for `test` its test invocation's, for `run` its runner's and its run checks'
runners' - and nothing without the option, so naming a set-up changes no run until a run asks for
it: a stack a developer keeps running by hand is never brought down under them. The option on a
run whose selected legs name no set-up is refused, `10`, since it would change nothing while the
command line says it did; and every line a leg would run, both ways, is resolved for that leg's
system before any host is measured (`LegSetUps.Require`, through the one `ISetUpService.Resolve`
the commands use): no line for its platform, a program not allowed, values its inputs cannot be
given, or one set-up a leg names twice with other values is refused, `13`, every one named with
its leg - none is found out once a leg's tree is synced and its earlier set-ups are up. A host is
handed `--set-up` only for a leg whose work names a set-up, and asked for each line's program in
the survey (`LegPrograms`), so a leg is never placed where its set-up cannot start. `build` and
`check-mutations` bring up nothing: neither runs what a set-up serves.

**A set-up runs where the leg's work does.** On the machine that runs the leg - a host is handed
`--set-up` with the rest of the command line - in the tree the leg works in there: the tree, its
copy in `.legs`, or a host's copy - as the configuration that tree holds declares them. The set-ups come up in the order named, before the leg's build,
and go down in the reverse order after its work, whatever its verdict, as one may stand on another.
Each line runs as a phase of the leg (`PhaseRunner`): in the leg's environment, under
`defaults.stallSeconds`, its log `set-up <name> up` or `set-up <name> down` among the leg's
records. One that does not come up fails the leg, its work never started, and every set-up the leg
brought up is brought down, the one that failed among them - unless its line never started, which
brought nothing up. One that does not come down - its line failed, it stalled, or the grace cut it
off - is said on the leg's line and carried in `--json` as the leg's `setUps`, and the run's last
line and its document's `setUpsLeftRunning` name the set-up, the leg and the `down` command, with
its values, that brings it down; where nothing failed the run exits `21`: what it started may
still run, and is a change begun and not finished. A leg stopped with no line of its own still says
what it did with its set-ups (`LegLedger.SetUpsEnded`), in the run's last line, or beneath the
refusal that ended the run, and a host's leg in that state tells this machine as its host's ledger
names it (`RemoteLegRunner`). A run asked to set up passes a point of no return
(`PointOfNoReturn.Holds`), here and on a host (`HostAgentService`): interrupted - while its work
runs or while a set-up comes up - its legs bring down what they brought up until shortly before
the command line stops waiting, counted from the interruption (`PointOfNoReturn.StoppedAfter`, as
an interrupted deletion stops), so a down line still running then is stopped and its set-up said
as not brought down before the process ends.

**Legs sharing a set-up share it.** A set-up in one tree of one machine is held by every leg using
it, in one run or several - or, where it says `"shared": "machine"` (`SetUpConfig.Shared`), in any
tree of the repository on the machine: what a machine holds once, a container's name or a port,
brought up by tree would be brought down by the first tree to end under every other. By tree is the
default, since a set-up serving one tree's own build shared with a leg in another would run that
leg's work against the other tree's code. Another repository's set-up of the name is another, its
lines its own: each hold records the repository its tree is of, as its main checkout - or the copy
of it a host keeps - is spelt there (`IHarnessContextLoader.RepositoryOfAsync`), the same whether a
run works in the tree or `down` is typed in it. A hold that says the machine shares it meets every
leg of the repository holding that set-up on the machine, whichever way theirs said (`SetUpHolds`).
Each set-up as it is up is an instance, named as it comes up and held by every leg joining it, and
the last leg holding the instance brings it down, whichever legs each shares with; a leg sharing it
with two instances up apart - two trees' legs that each brought it up by tree - waits until it
meets one. Holds are recorded in `<user data>/dssharness/setups-<machine id>.json`
(`SetUpHolds`), beside the admission record, each change made under the file's machine-wide lock
(`MachineWideList`). The first leg
brings it up; a leg finding it coming up or going down waits until it is up or down, since its work
would otherwise run against services not there yet or about to go; one finding it up with the
values it gives joins, and one giving other values waits until the legs using it let it go, then
brings it up with its own; and the last to let go brings it down. A leg holds every set-up it
names before any comes up, in the order of their names - two legs naming two in opposite orders
with other values would otherwise each hold one and wait for the other for ever - and the values a
use gives are read as the inputs take them (`LegWorkload.SetUpsOf`), so one set-up named twice
with the same values, however each is written, is one. A hold is its process's, so one whose process has ended is let
go of when the record is next read, and said so. A run killed before bringing a set-up down leaves
it up, and the next leg brings it up again over it - so a set-up's lines must be safe to run twice.
`down <name>` is refused, `13`, while a live leg holds that set-up in that tree - or in any of the
repository's, where the machine shares it - naming each: brought down under them, their work would
fail for a reason nothing of theirs says.

## Mutation testing

`check-mutations` is the mechanical proof that a repository's tests can fail. For every selected
leg and every arm its registry declares, it mutates exactly the text the arm names, builds the
arm's target, proves every object that depends on the site was rebuilt, runs the arm's test binary
whole, judges what reddened against the declaration as an exact set, and puts the site back,
checked by its hash. A BUILD-RED arm must instead stop the build at an object that depends on its
site, and its paired positive control must build. All of it happens in worker copies of the leg's
tree, kept in the tree's `.mutations`, never in the tree's own files, which a sweep only ever
reads. A leg's verdict is the worst of its own -
its workers' builds and its test binaries' controls - and its arms' (see *Verdict vocabulary*).

### The registry

`mutations.registry` names the repository's own file, one row to a line, its fields separated by
`|` and trimmed, a line whose first character that is not blank is `#` a comment, nothing escaped
and the last field taking the rest of the line. An A row declares an arm - its site, the files
holding its before- and after-text, its red kind, its target and runner, its case count and its
diagnostic - and C, G, B, M and S rows, each following the A row of its arm, add a case that must
redden, a neighbour that must run and stay green, a BUILD-RED arm's paired control, another site
mutated with it, and the legs it runs on. An M row's site is another file, or - naming a file the
arm already mutates, as the tree's own file system compares names - a further text of that file
(`MutationSite.Further`), which it must spell as the arm's first row for that file spells it: the
one spelling held to the tree's, so a row that names the file otherwise is the registry's problem
at its line. A consumer's mutants were several places of one file, thousands of lines
apart, either edit alone another program or no change at all, and one text spanning them would
break at any edit of a line between. Every text of a file is replaced together
(`SiteEdit.ApplyAll`): each is looked for in the file as the tree holds it, never as another of
them left it, must stand there exactly once and differ from what replaces it; two that share a
byte of the file are the registry's problem at the later row's line, exit 12, before any host is
touched, and an arm's violation where only a worker's copy shows it. The file is written once,
put back whole and checked once against the tree's, and what depends on it is witnessed rebuilt as
for one text, so its verdicts are judged as any arm's. A BUILD-RED arm carries such a row for its
own file alone, which its paired control rewrites whole. A
text is a file in `mutations.textDirectory`, read as it is held, less a UTF-8 byte order mark at
its start and one line ending at its end, and given the site's line endings where the site ends its
lines otherwise, so one registry serves a checkout with either. Every file directly in that
directory must be cited by some row; what lies below it is not listed, and a file there that a sync
withholds - one git ignores, as a desktop's or an editor's leftover is, or one the configuration
keeps from a sync - is no text: no copy of the tree holds it, so nobody could drive it.

The registry's text has one reader, `RegistryText`. It keeps every line with the ending that line
has, a line ending exactly where `string.ReplaceLineEndings` reads one - the one set of endings
this tool divides lines at (`TextLines`), which a phase's last lines are divided by too - so a
problem's line number is the one an editor shows whichever way the checkout ends its lines; and it
says of each line whether it is blank, a comment or a row, and cuts a row into its kind and its
fields (`RegistryLine`), which is the one place a row is trimmed and split and what the parser
reads through. A row of any kind belongs to the arm its second field names. Read from the bytes a
tree holds, the text is UTF-8 and nothing else (`Utf8Text`, the one strict reader, which an anchor's
cell file is read by too) - a byte that forms no character is why it is not read, never a character
put in its place - a byte order mark at its start is noted and is no part of its first line, a
line that opens with another is why it is not read, since standing first it would be read as the
text's own, and the text is written back as the bytes it was read from.

What makes a text a registry a sweep can read has one owner too,
`MutationRegistryValidation.ValidateAsync`: the rows and each arm's shape, the cover of the text
directory, each cited text, and each scope's legs. It reads a tree through a view of its files
(`IRegistryFiles`), never the file system itself. `TreeRegistryFiles` is a tree as it stands, and
is made one way (`OfAsync`), which asks what a sync of that tree withholds, once.
`OverlaidRegistryFiles` is a tree with some paths written and some removed: each written path is
given as what reads its bytes when they are asked for, so nothing is read that no row needs read,
and nothing twice, and a written path is there, listed in its directory and not withheld, whatever
stood under it before. So a text nobody has written yet can be judged by the tree it would stand in, with the
sentences `check-mutations` refuses a registry in (`RegistryValidation.Refusal`), and
`MutationService.ReadAsync` keeps what is about the file itself - that it is configured, a file,
readable and carried - before the validation, and weighs `--arms` and the report arguments after it.

A registry two sides both changed is merged by arm, never line against line
(`MutationRegistryMerge.Merge`, which a fold, a rebase and a refresh call with no option given -
see *Seeding and folding*): a function of three texts' bytes - what both sides last held, where
they held one, the main tree's and an agent's - that reads no file, no clock and no configuration.
Each text is read, through the one reader, as the lines above its first row down to their last blank
line, its segments - each one row with the comment and blank lines directly above it - and the
lines below its last row. The comments a blank line parts from the row below them are a heading - a
section's header - and never that row's arm's, while the comments abutting a row are its arm's own.
An arm is the segments naming it, wherever they stand, ids compared ignoring case, and two versions
of an arm are the same where each of its own lines' text is, the blank lines and the heading that
open each segment left out: they only part it from what stands above it, so an arm removed or added
beside another, or before a section's first arm, changes nothing of that one. How a line ends is no
part of it either, so a checkout with other line endings changes nothing. Each arm is taken from the
side that alone added, removed or changed it, and one both changed alike is one. An arm each changed
its own way, one a side removed and the other changed, or one both added differently - under ids
that differ only in case, among them - is a conflict (`ArmConflict`), said with each side's lines
that the versions do not all hold and each line's number, the three texts called what the caller
calls them (`MergeSides`). With no text both held, an arm one side has is kept: its removal by the
other could not be told from its addition. The lines above the first row, those below the last, and
each heading are each weighed the same way, as one - a heading as the one above an arm's segment in
its place among the arm's segments, where a merge lays one side's segments of an arm over the
other's: a heading one side alone renamed or removed is that side's, one a side moved to another
arm's row is removed above the one and added above the other, and one both changed differently is a
conflict over the heading, named for the arm whose row it stands above
(`ArmConflict.OverTheHeading`). A text that is not UTF-8, holds a row naming no arm, or holds
a line that opens with a byte order mark - which the one reader refuses, since standing first it
would be read as the text's own - is not merged at all, and the result says which of the three and
why.

The merged text is the main tree's, line for line, changed only where the agent contributed. The
main tree's segments stay where they stand, each written under the heading taken for its place - or,
where none is, below the blank lines the main tree parts it from what stands above it with, one of
them where a heading stood there. An arm taken from the agent in place of the main tree's lies over
the main tree's segments of it, one for one, in their places: where the agent has more, the rest
follow the last, and where it has fewer, the main tree's remaining places go. A heading only the
agent added, whose lines are a heading both held that the merged text already holds above a segment
of the main tree's, is that one heading - the agent wrote it above a new first arm of its section -
written once, and an arm only the agent has that stands under it joins that section after the arm
the heading stands above. A heading taken for a place no segment of the merged text stands in - its
row dropped by one side, the heading kept or changed by the other - would be read back as part of
what stands below it, and is a conflict of its own (`ArmConflictKind.HeadsNoArm`). A segment of an
arm only the agent has stands after
the nearest segment above it in the agent's text that the merged text holds - a segment of an arm
kept from the main tree stands as the main tree's like it, or as the arm's last where no later
segment of the arm is held; where the main tree's version holds none like it, it stands as the main
tree's segment in its place among the arm's - the arm's last for the agent's last - since the main
tree reworded it, unless that version holds fewer segments of the arm, having dropped it, and then
it is not held - and past what the main
tree alone holds there, so two sides adding at one place come out the main tree's addition first,
then the agent's; the agent's first segment stands first, past what the main tree alone holds there;
one above which the merged text holds nothing goes after the last segment; and none stands above an
earlier segment of its own arm. The segment that comes to stand first is written without the blank
lines that open it. Each of the main tree's lines is written as
the main tree holds it, each of the agent's with the ending of the main tree's first line, behind a
byte order mark where the main tree's text has one and with an ending on the last line where the
main tree's has one.

So a text merged with itself is itself, and a merge in which the agent changed nothing is the main
tree's text, each byte for byte; one in which the main tree changed nothing holds the agent's arms in
the main tree's order, with its line endings and its byte order mark. A merge run again with its own
result on either side gives that result, byte for byte - what a command needs that wrote the result
to one tree and was stopped before the other. That holds because a merge is offered only where its
text reads back as it was merged, and each heading of the result stands in the place the side it is
taken from stood it, so the merge of the result knows it as that side's. Where the lines
above the first row end is a matter of where a blank line stands, so a segment that keeps a blank
line above its row and would come to stand first is a conflict of its own
(`ArmConflictKind.WouldOpenTheText`), as lines with a blank one among them are where no row would be
left above them; an empty last line is ended whatever the main tree's is; and an empty line that
would follow a lone carriage return is ended by a carriage return and a line feed. What the merge
offers is a text: whether it is a registry a sweep can read, in the tree it would stand in, is the
validation's to say.

The whole registry is read, with every text it cites, before any host is touched, and every
problem is listed with its line, exit 12: a sweep refused from inside a leg would end the run once
its hosts were measured and its slots taken. What a sync withholds is in no copy of the tree, and a
worker is such a copy, as a host's is: so a registry or a text directory under `sync.neverTransfer`,
`sync.exclude` or the worktrees root is refused when the configuration is read, as one inside the
harness's own directory is, and a cited text so withheld is a problem of its row - never every arm
read `violated` for a text nobody carried. What git ignores a sync leaves behind as well, and only
the tree says which that is: so git is asked as the registry is read, and a registry or a cited
text it ignores is refused the same way - one it tracks is carried, whatever rule would ignore it.
The rows a mutation harness of a repository's own once
needed and this one derives - R, X, I, F and T - are refused, each naming what took its place: the
leg's own tree, project and variant; sync's exclusions; the variant's configure; the dependency
sources the leg's own build fetched; and ninja's records. A leg built by anything but CMake with the
Ninja generator is refused too, naming the fix: only ninja's records say, for one configuration
alone, which objects a mutation rebuilt. `--arms` selects arms as `--legs` selects legs, and an arm
it leaves out, or whose S row leaves the leg out, is `skipped-not-selected` on that leg, as is one
`--pending` leaves out because its `passed` holds. It is
weighed against the registry only once the registry can be swept (`MutationService.ReadAsync`): the
S rows' legs are resolved with the rest of the registry's problems, so a scope naming neither a leg
nor a leg set is listed beside them in one refusal, exit 12, whatever `--arms` names - an arm the
registry does not declare, exit 10, hid it, in the one command that reads a registry and stops. An arm
selected whose S row names no selected leg is warned of before the sweep starts; and a selection
whose every arm is such a one is refused, exit 10, naming each and where it runs, before any host
is touched: every leg would be skipped, and the run would pass having swept nothing. A host sweeping
one leg of a run refuses nothing of the kind, since only the machine that selected the legs sees
them all.

### Workers

A leg's workers are copies of its tree kept in it, `<tree>/.mutations/<key>w-<n>`, a family
of copies of their own in the slot `init` keeps for them: in git by its placeholder, with
everything made in it ignored, withheld by every sync and never deleted in a copy, and passed
over by every reader of a tree - the reading a sync or a sweep makes, the input watch, the count
of uncommitted changes, what `clean` measures, and a fold's weighing of an agent. Each worker is a
repository of its own, as every copy a sync makes is, its HEAD the tree's and its index what the
sync carried, so git and the harness asked in a worker answer for the worker, never for the tree
around it. A sweep, or a self-test, in a tree whose git does not ignore what it would make in
`.mutations` - a branch whose rules predate `init`'s - is refused, exit 13, naming `init`, with
nothing made: asked on the machine that makes the workers, so a host answers for its own copy. The key is the first 7 hexadecimal digits of the SHA-256 of the leg's
variant, as its build directory is named, so the machine that dispatches a leg and the host that
sweeps it name its workers alike - and a key, never the variant's name. A worker is a second tree,
built as deep as the first, so where paths are bounded every character its name adds is one the
tree's own path must leave free; spelt out, a variant's name stood in a worker's path twice, in
its name and in the build directory within it, and a repository whose path budget is reckoned to
the character had room for neither. Its first worker adds twenty-two characters to the tree's path
whatever its variant is called - two more than one kept beside the tree did, the slot's separators -
and the lines of a sweep and of a `clean` say which leg a worker is. Two variants of one
tree whose names came to one key would share their workers and their lock, and neither would be
judged on the other's: each sweep syncs a worker again by content and builds in its own variant's
directory within it. Workers an earlier version of this tool kept beside a tree,
`<tree>.mutation-<key><mark>-<n>` (`MutationWorkers.FormerFamily`), are no workers to a sweep:
nothing counts them, and `clean --former-workers` alone removes them - beside each tree of the
repository on each machine a selected leg runs on, once a machine, and beside each tree deleted
since, whose workers show its name (`LocalSyncTransport.FormerWorkerTrees`). Here that is the main
checkout and every worktree git records (`IRepositoryTrees.HereAsync`), and the worktrees root and
each directory of it, which hold the repository's worktrees alone; each tree once, by where its
workers are with the links to that directory followed, so two spellings of one are one. On a host
it is asked of the host from its home (`ISyncTransport.RemoveFormerWorkersBesideCopiesAsync`),
beside its copy of the main checkout and each worktree copy beside it, under that copy's name or a
worktree copy's, there still or removed since - whatever copy the host holds, none even. Each goes
as workers kept in a tree are removed (`LocalSyncTransport.RemoveFormerWorkersAsync`, the one
removal of copies kept together): by its marker, a dry run listing each with its size, one that
version's sweep still claims kept by the claim file beside it, refused-locked, and one no sync made
said and left. Where the worktrees, or the trees deleted since, cannot be listed, those beside the
trees that are known are dealt with and the leg fails, saying so; a leg's copy of a tree is no tree
any version kept one beside.
Until then, one below an orchestrator's directory is said as any
directory there holding a `.git` of its own that git records no worktree at, and keeps the
orchestrator from being deleted until it is gone; one beside a host's worktree copy is spelt as no
copy's name, so a listing of the host's worktree copies never holds it. A sweep runs
`mutations.workers` of them (2),
never more than the leg has arms to drive, and one in a WSL distribution, whose sweep this machine
admits whole. The tree is read once, as a sync reads it, and every worker is synced from that one
reading, so every arm measures the same tree however long the sweep takes, and an edit made
meanwhile reaches none of them. A worker stays between sweeps and is synced again by content - so
its build directory stays warm, and a site a killed sweep left mutated is put back - and built
whole as the leg builds before it drives an arm. That sync withholds the worker's own build
directory itself, whatever `sync.neverTransfer` says: the sweep made what is in it, no spelling of
a configuration should decide whether a sweep may run, and `sync.maxDeleteFraction` judges only
what mirrors the tree the sweep read - a genuinely wrong source tree is still refused by it, with a
remedy a reader who typed `check-mutations` can take. It withholds that directory, the one the
leg's variant builds in under `build/`, and no more: a file the tree carries elsewhere under
`build/` - a toolchain file it tracks - is carried and verified in the worker as any other, and
one the tree keeps inside that directory is not carried at all. A worker is claimed while
a sweep uses it, in `<worker>.claim.json` beside it in `.mutations`, by the claim a run takes on its log directory,
which never makes the copy it claims: the sync that makes a copy refuses a directory it did not
make. A claim whose sweep died is released and said; the copy it held is synced again, as every
worker is before it drives an arm.

A worker needs its copy of the tree - its files, and what git keeps there of its history: the commit
the tree was at, and every commit behind it under `sync.history: full` - and what a build of the
variant comes to - the leg's
`buildSpaceGiB` where it declares one, else what the leg's own build directory last recorded, else
the most any other tree of the repository on its machine recorded of it - less what it already
holds. The
sweep runs the workers that fit the room, in order, saying it runs fewer; where not even the first
does, the leg is `skipped-unavailable`, as a leg whose build does not fit is. A worker's build is
kept within this machine's path limit as a worktree's is - the worker, its build directory, and
`worktrees.pathBudgetReserve` below that, with the margin to spare - since a build past it fails as
compile errors in files nobody touched. A sweep that runs fewer says how many of those it wanted -
`mutations.workers`, no more than its arms - and what keeps each of the rest out, the room, the path
limit, or both. Workers an earlier sweep left beyond `mutations.workers` are
removed before a sweep plans, so lowering it frees the room they held; a directory under a worker's
name that no sync made is said and left.

The workers go with their tree, kept in it. `clean` removes a leg's with its build directory (see
*Disk space*). `delete-worktree`, and so `delete-agent`, removes the workers kept in the worktree
before the worktree itself, and each host removes those kept in its copy before the copy
(`remove-workers`, served as every sync operation is): each worker a sync made, of whichever
variant, with what an unfinished removal left aside - looked for in the tree's own slot alone, and
only what is named as a worker's there. Each worker answers
for itself: one that cannot be removed is left, saying why, and the others still go; one gone by
the time it is reached was somebody else's to count; and a removal that is stopped answers with
what it had done. A worker a sweep still running holds says its tree is in use: the worktree is
not deleted, nothing of it removed, and a host's copy stays, still recorded - and one that cannot
be removed, or told for the harness's, keeps its tree as well, as a failure rather than a tree in
use - or `--force` deletes the worktree with every worker in it, said. A directory in the slot
under a worker's name that no sync made goes with the worktree, or the host's copy, as everything
else in it does, and is said. A claim a sweep took while its worker was being removed is that
sweep's, and stays. A worktree whose own removal then fails, or is stopped, says which workers went
before it. A worktree already gone, an agent whose worktree is gone, and a host holding no copy of
the tree hold no worker: they went with it.

### Fetched sources

A worker builds the dependency sources the leg's own build fetched: each `FETCHCONTENT_SOURCE_DIR_<NAME>`
entry of the leg's CMake cache whose sources are there - in `<FETCHCONTENT_BASE_DIR>/<name>-src`,
or where the entry names - and with every declared dependency found, fetching is turned off, so a
worker the network cannot reach still configures. One not found is fetched by the worker as the leg
would fetch it.

They are carried into each worker rather than pointed at where they are. They sit in the leg's own
build directory - where the leg's configuration places that build for this machine: the copy of
its tree it works in where its `localCopy` key or the repository's default says one, its tree
itself where neither does - and the sweep's lock deliberately leaves it to a `clean` or a `build`
of the leg: pointed at in place, a clean mid-sweep failed every later arm's build, and a build
that fetched again measured later arms against other sources than the arms before them - each
blamed on the arm. So they are read once, as the tree is, by size and hash, and each worker is
given its own copy by
content, in `<worker>/.harness-config/deps/<name>`: the harness's own directory, which no sync of
the tree carries or deletes and no build from clean removes, under no longer a path than a build
directory keeps them under. A file that moved since the reading stops that worker being made, as a
tree that moved stops its copy, and the other workers go on. What a worker keeps of a dependency it
is no longer given is removed. Their files are carried, never a clone's own `.git`, as no copy of a
tree carries one; a link among them is carried by no copy, and is said on the leg's line.

Sources the tree itself holds - a directory of it the leg was pointed at - are the worker's own
copy of that directory, which its sites are mutated in; and a dependency the project's own
`cacheVars` point at is the project's to say, in a worker as in the leg. Named there in another case
than FetchContent reads - a name of the configuration compares ignoring case, a cache variable's
does not - it is left to the project still, and fetching stays on, so the worker fetches it as the
leg's build did.

Every configure of a worker first removes each dependency's source directory, and what turns
fetching off, from its cache (`-U`), then sets what this sweep gives: a build directory kept between
sweeps would otherwise go on holding what an earlier sweep's configure gave it - a directory the
worker no longer keeps.

### An arm's turn

Each test binary's arms wait for its pristine control, built and run once on the leg in whichever
worker first needs it: a control whose build does not pass, or with a red case, no report, a
failing exit or a hang, decides the leg's own verdict and stops every arm of that binary, saying
why, since no mutation of it could prove anything. Its run bounds theirs (see *Timeouts*). Workers drain one queue of arms. Each arm:

1. is admitted as a unit of its own, where its machine declares admission (see *Heavy legs share
   a machine*);
2. is pre-flighted against the worker's copy, the sweep's reading of the tree and the ninja records
   of the worker's whole build, before anything is written: its sites there, each spelt as the tree
   spells it - a file system that folds case would find one spelt otherwise, and nothing could then
   vouch for it once put back - and held by the reading, never a file a build made in the worker;
   its texts there; each before-text occurring exactly once - its own, an M row's, and a paired
   control's in the site as it was - and each replacement changing something, since a mutation of
   nothing reddens no test and would read as one that survived; its target and runner built; and
   some object its build builds depending on a site, through ninja's dependency records, so headers
   and precompiled headers count;
3. is mutated, each site written dated past the newest file the worker's last build wrote, and the
   build waits until the clock is past that, so no object is dated before its source - save where
   that file is dated more than a minute ahead of the clock, which went back from it: waiting it
   out could take hours, so the site is dated by the clock, and the build's own rule, finding an
   input dated no later than what its last build left, starts from clean, saying so;
4. is built - its target, and a TEST-RED arm's runner beside it, so the binary run links the
   mutation - through the build every leg builds by, every guard included;
5. is witnessed: a build that failed is read for the steps ninja said failed, and one that passed
   is held to ninja's log, read before and after, for every object depending on a site;
6. is run whole with `mutations.reportArgs`, its JUnit report a new file for every run, or for a
   BUILD-RED arm, has its paired control applied to the site as it was and built. The sweep starts
   the binary itself, never through ctest: see below;
7. has every site put back and checked against the reading of the tree by its hash, whatever
   happened - never stopped, so a sweep stopped part way still puts its sites back, and each site
   on its own, so one that cannot be written keeps no other from it. A site that cannot be put
   back, or read back, makes the arm `poisoned` and retires its worker; the other workers go on,
   and what no worker drove is `stopped`, saying why.

The judge is a pure function of the declaration and what was observed, the first row that applies
deciding, in the order the steps above observe it; `help mutations` lists what each verdict means.

A report is the runner's own record, and what is wrong with one is said as what it is. One that
is no report - no XML, a root that is no `testsuites` or `testsuite`, a document type declared,
which is never read - is `unattributed`, its line saying which: a runner writing another format
and a report a crash cut short read differently. One that is there and cannot be read from its
file - another process still holding it as the run ends - is read again, five times in a second,
and then `unmeasured`, saying why: that is this tool's failure to read, and what the file holds
may name every case. The same holds of a binary's unmutated run, whose arms are then stopped.

A runner is started by the sweep itself, never by ctest: the program the runner's target builds,
in the worker's build directory, given `mutations.reportArgs` alone, in the directory the leg's
tests start in (`test.workingDirectory`, filled for the worker, else the worker's root) with the
environment they start with - the host's declared `env`, the developer environment the toolchain
names, `test.env` over both, the host's program directories on `PATH` (`MutationLegRunner.RunRequest`).
Nothing else of the test settings is applied - not the runner, its args, a filter, an exclusion, a
label, the core count or the success pattern - and nothing of a test's ctest entry: not the
`add_test` command line, its launcher or its arguments; not `ENVIRONMENT` or
`ENVIRONMENT_MODIFICATION`; not `WORKING_DIRECTORY`; not a fixture (`FIXTURES_REQUIRED`,
`FIXTURES_SETUP`, `FIXTURES_CLEANUP`), `DEPENDS`, `REQUIRED_FILES`, `RESOURCE_LOCK` or `RUN_SERIAL`;
not `TIMEOUT` - a mutated run is bounded by `mutations.runTimeFactor`, an unmutated one by
`defaults.stallSeconds` alone; and not what ctest reads a result by - `WILL_FAIL`,
`PASS_REGULAR_EXPRESSION`, `FAIL_REGULAR_EXPRESSION`, `SKIP_REGULAR_EXPRESSION`,
`SKIP_RETURN_CODE`, `DISABLED`. A case is red where the binary's own report says so, so a test
that is green under ctest for what ctest sets up around it can be red in its binary's unmutated
run.

Where that happens, each arm the control stops names the control's run log on its line, and the
binary's own line names what ctest registers for the red cases that a sweep does not apply
(`CtestEntries`, `CtestDescription`): the command the test command of the same settings makes,
`--show-only=json-v1` appended, started as that command starts in the worker whose build ran the
control, bounded by two minutes, once per runner per sweep - as the control itself is - and only
where the leg's tests run through ctest. The red cases are matched to the tests the description
registers for the binary - the one test that starts it; the test a case names, by its name or an
argument; no way to tell, counting what the tests declare; or no test at all - and what of the
matched test's entry a sweep does not apply is said with it, an environment by its variables,
never their values, a working directory only where it is another than the sweep's own. What ctest
could not say is said as that and changes no verdict; beneath the leg, `--json` carries each
control that did not pass as `controls` - its runner, verdict, detail, reds, records, and the
match as `ctest`.

A mutated build is a build of its own. Each mutation is configured and built again in its worker,
so whatever a project derives from its sources as it builds - a stamp that digests them, a version
header, a cache named for either - is another in every mutant than in the unmutated build. A case
that depends on such state is red under every arm of its runner, and no arm declares it.

Whatever fails is kept to what it failed in, so one failure never costs the arms already judged. A
worker that cannot be made - its copy, its whole build - is retired alone, saying why, and the
other workers drive every arm; only where none could be made are their failures the leg's own.
An arm whose driving ends in a failure is given the verdict that failure comes to, as a leg whose
work ends so is: the verdict a refusal names, `failed` for a program that will not start, and
`poisoned` where nobody named it - its worker then retired, since nothing vouches for its copy,
unless the failure was a file its pre-flight could not read, before anything was written. A binary
whose control cannot be built and run stops its own arms and no other.

A machine that does not admit one arm has kept it waiting as long as it allows, and would keep the
next as long: so the sweep asks it for no other, and every arm left - one still waiting among
them - is `not-admitted` at once, naming the arm that was refused. A worker's own unit refused ends
that worker alone: it claims room, which no arm does.

A sweep stopped part way puts every site back, gives its workers up, and still answers with the
leg's line: each arm judged by then with its verdict, and the arms it was driving and those no
worker reached `stopped`, each saying which. The run ends interrupted, exit 130, as any run does. A
refusal of the run raised inside a sweep - a setting that cannot be satisfied, a record of the
machine's that cannot be read - ends the sweep the same way and then the run, with the refusal's
own exit code, once the leg's line is recorded: what the sweep had measured is reported before the
refusal, as a leg that finished before it is.

### Locks, records and hosts

A sweep of a leg takes a lock of its own: its workers' key, whole, on the leg's host. It refuses
another sweep of the leg's variant, `refused-locked`, and a `clean` of its workers, and never meets
a build, test or sync of the leg, keyed by its tree, which a sweep of hours must never hold off.
`--force-lock` takes it, and a worker a live sweep claims.

**On another machine a sweep works in a copy of its own**, so neither holds the other off in either
direction. A host's copy of the tree is synced, taken whole, by every command whose leg lands there,
and a test holds it shared for as long as it runs: a sweep that synced that copy was refused for as
long as a test ran on it, and a test asked while a sweep synced it was refused in turn. Instead the
sweep's leg works in `<host's copy>/.legs/<key>` (`LegTrees.For`, asked by placement and by the room
a host is asked about alike), synced from this machine's reading of the tree as the host's copy is,
never adoptable, its workers kept in it - and a sync of the host's copy never deletes it, as no sync
deletes a copy's own copies. The sweep holds that copy whole (`MutationWorkers.SweepLock`), so a
second sweep of the variant there is refused-locked before its sync rewrites the copy; a build, a
test or a sync of the host's copy never meets it, nor it them. Where the host's copy is not there
yet as a copy this tool finished, the run syncs the tree to it first (`LegRunService.SyncHolderAsync`),
as any command would - a directory this tool did not make refused as any sync refuses one -
once a run however many legs keep their copies in it, under the lock any sync of that copy takes,
so another run's sync of it is never run beside this one and refuses these legs, refused-locked:
a clean, a test or a deletion there finds the tree, never a directory holding nothing but the copy
kept in it. The host's agent serving a sync of a copy kept in a tree's slot starts in the tree,
since a sync withholds the slot and the first copy kept there makes it. The DssHarness on the host runs in that
copy, a repository of its own whose lock is its own: so whatever removes a copy kept in a tree's
`.legs` asks that copy's lock as well as the tree's (`LegCopies.HeldBy`), `clean` of the leg here
asks this machine's lock for the copy before the host is sent anything, and a removal of a host's
copy meets the workers in each copy kept in it (`RemoveWorkersAsync`), a claimed one keeping it.

Each arm's records are written as it ends, in `<run>/<leg>/arms/<arm>/`: `arm.json`, its line as
the ledger carries it, beside the logs of its build and its run, and of a paired control's build in
`control/`. Each control's are in `<run>/<leg>/controls/<runner>/`, and each worker's whole build in
`<run>/<leg>/workers/<n>/`. A leg's line counts its arms by verdict; below the table an ARMS block
names each selected arm that did not pass, and why; `--json` carries every arm beneath its leg - `arm`,
`verdict`, `failure`, `detail`, `durationSeconds`, `worker`, `cases`, `declaredCases`, `reds`,
`declaredReds`, `records`, `key` - a skipped one included, and each control that did not pass as
`controls`, with what ctest registered for its red cases.

A leg on a host is swept there, by the DssHarness there, on its own copy - with its own workers,
its own admission and the arms `--arms` named - and its arms travel back beneath its line, their
records staying on that host and its home written as `~`, as a leg's are (see "A host's home is
`~`"); the answer changed shape, which raised the host-agent protocol to 6.

### What an arm is judged on

Each arm's line carries its `key`: the SHA-256 of what it was judged on (`JudgedOn`), ten parts
written as canonical JSON - the options every state file is written with, on one line, each map
sorted by its keys as ordinal strings. Where all ten are what they were, nothing this tool reads
that the verdict rests on has moved. That is not to say nothing it rests on has: what a test binary
reads at run time that `mutations.runReads` does not name - a data file, a plugin it opens - is
read by no part, so editing it moves no key, and a recorded `passed` it would change still holds.

- `judge`: `ArmJudge.Revision`, raised by hand whenever a change could give another verdict for the
  same inputs - in the arm's judge, the unmutated run's, the pre-flight, how a site is edited, how
  a rebuild is witnessed, how a report is read or a diagnostic looked for - or changes what a key
  is made of. A test holds it to a digest of both judges' verdicts over a fixed table of
  observations, so a rule of theirs cannot change with the number left as it was.
- `rows`: one digest to each kind of row the sweep and the judge read - A; C and G sorted, as the
  judge compares them as sets; B; M in the order declared, as each text is found in that order.
  Never a why, a comment, a line's number, the letter case of the id, or the S row, which says
  where an arm runs, while what it was judged on is kept leg by leg.
- `texts` and `sites`: what the sweep's one reading of the tree says each text the arm cites and
  each site it mutates holds, found as the tree's file system compares names, or nothing where it
  holds none - a text the arm cites that the reading does not hold leaves it no key, since what it
  was judged on was not read whole. No file is hashed a second time.
- `built`: the digest of the table of each target the arm's build builds (below).
- `compilers`: each compiler CMake identified in the worker's build - its id, version, program and
  the words it is run with.
- `developerEnvironment`: the leg's, as it was set up, where its toolchain names one.
- `settings`: three digests (`SweepSettings`) - what the build is given (the variant's name, the
  environment its overlay and its host give it), how the run starts (the working directory and
  the environment the test settings declare for the leg's system, the report arguments), and what
  the judge is given (`mutations.runTimeFactor`, `defaults.stallSeconds`). Every other value either
  cannot change a verdict - the workers, the cores, admission, the path limits, the clock's
  tolerance - or reaches an arm through another part: the cache variables, the build type and the
  generator through the build lines, `sync.*` through the reading, the tool search directories
  through the compilers, `mutations.runReads` through the files it names.
- `runReads`: each file of the tree the arm's runner reads at run time, as `mutations.runReads`
  names them for that runner - a path, everything below a directory, or `**/name` at any depth, as
  every list of paths reads them (`PathPatterns`), and found as the tree's file system compares
  names - with what the sweep's reading holds of each; nothing for a BUILD-RED arm, which runs
  nothing. Declared, never found: what a binary opens at run
  time is said by no record a build keeps, and every file of the tree in every key would make every
  arm pending after every edit. A file that joins or leaves what the entries cover moves the key, as
  one that holds something else does - and each is named by the path the reading spells, so a file
  renamed by case alone, where the tree's file system folds case, is one that left and one that
  joined, compared as the key spells them, never as the tree finds them (`KeyDifference`), as a file
  of a target's table spelt otherwise changes what it is built from; an entry that covers no file of
  the reading leaves the arm no key, naming the entry, as a text it cites that the reading lacks
  does. A runner no TEST-RED row of
  the registry names, one given no file, an entry outside the tree, one holding a `*` anywhere but a
  leading `**/`, and one naming the whole tree, are refused - the runner when the registry is read,
  the rest when the configuration is.
- `host`: the host the leg ran on, as the machine that dispatched it names it.

A target's table (`BuiltFromTable`) is what the build of it read, never what that was generated
from: its build lines - the manifest's closure of the target, and each line making a file they
read that the closure does not reach, as a header a command generates, which a compile is only
ordered after - and every file those lines read as ninja's records say, by the walk that says which
objects a mutation rebuilds. A file a build line makes is left out: what it is made from is read
already. A file of the tree is taken as the reading holds it, found as the tree's file system
compares names and named as the reading spells it. A file of a dependency's sources the worker was
given stands as the whole dependency, its name and the digest of the sources as the sweep read
them. A file the worker's configure made, and one outside the worker - a system's header, a library
a link names - is hashed where it is, once a sweep, and counted and digested with the others of its
directory. So a list file edited for another target moves nothing of this one, and a source a glob
finds joins the table once a configure finds it, as a worker's does on each of its builds.

Every worker of every tree of a leg must come to one table for the same content, so the worker's
own root is told - replaced by `{tree}` - wherever it stands in a build line or in a file its
configure made: as its machine spells a path, with `/` throughout, with each `\` doubled, and with
its drive's letter in either case (`WorkerPath`). A file that holds the path another way never
compares equal across workers, which is the safe direction. No part of a key is a commit, a date, a
run, a worker or a tree: a worker made again after a `clean`, or a tree of the same content kept
elsewhere, comes to the same key.

The first worker made reads every arm's key, after its configure and build and before it drives an
arm, so what an earlier mutation's configure left in a worker never reaches one. A part that cannot
be read - a file gone or unreadable, a directory a build line depends on, whose files no line names,
a text it cites that the reading does not hold, a compiler's record unread - leaves the arm no key,
and its verdict as reached. A key is made one way (`ArmKey.Of`): what each target is built from is
the digest of the table read of it, so the key and the tables it names never disagree. The key travels back from a host beneath the arm's line, which raised the
host-agent protocol to 10.

### What holds, and what is pending

A sweep records each arm's verdict on each leg together with what it was judged on. A recorded
`passed` holds where what the arm would be judged on now, computed in full on the leg's machine, is
what the record says; every other arm is pending.

Beside the key, never in it, each verdict keeps the causes of the leg's build lines (`JudgedCauses`):
each file of the tree the manifest is generated from - the inputs of the build line that makes
`build.ninja` the reading holds, with what each holds - and a digest of what the configuration gives
the configure: the generator, the build type, the cache variables and the project's path. In the key,
every arm of a leg would be pending after every edit of any list file, which in a round of several
agents is every fold: the coarseness a consumer rejected for a commit id. The key holds their effects
on each target instead, and the causes are kept so what moved can be named.

The record is `.harness-config/mutation-record/` in the main checkout (`ArmVerdictRecord`, resolved
from `HarnessLayout.MainHarnessDirectory`), whichever tree swept: so an agent's sweep of its own
worktree counts for the main checkout wherever nothing an arm was judged on differs, and a deleted
worktree takes none of its verdicts with it. Kept in each tree, that would not hold; kept per user,
it would need a name for the repository, which only its path gives. It ignores itself, and
`HarnessDirectorySync.IsRunState` names it, so no sync carries or deletes it. One file to a leg,
`<leg>.json` - a sweep of one leg never rewrites another's verdicts, and a file that cannot be read
loses one leg - and beside them `built/<leg>/<digest>.json`, each table an entry names, written once
under its digest. An entry holds the verdict and its detail, the run, when, which tree swept it
(`sweptIn`, as a line names a tree: told, never compared), the key and its parts - or no key and
why - the causes, and the last sweep that found it holding (`verified`); the file indexes each table
by the digests of its parts, so which part differs can be named without opening it.

An arm keeps one entry to a key on a leg. A later verdict on a key replaces the entry, whatever
either was: a `passed` followed by anything else on the same key is no longer a `passed`. A verdict
with no key replaces the arm's entry with none. At most eight are kept (`ArmVerdictRecord.KeptPerArm`),
the oldest by its place in the file going first, never by its date: this tool serves hosts whose
clocks step. Each change is one read, merge and write under the file's machine-wide mutex: the
tables the entry names are written where absent, the entry merged, those beyond the bound dropped,
the file written whole, and each table no entry names any more removed - so a table is never written
ahead of an entry naming it, and two sweeps of one leg from two trees lose neither's verdicts and
remove neither's tables.

The sweep hands each verdict to its sink (`IArmRecordSink`) as the arm ends, so a sweep stopped part
way keeps what it had judged. Recorded: every verdict reached by observing the arm, and an arm
stopped because its runner's unmutated control did not pass - the newest evidence on that key says
the binary is not green. Not recorded: an arm its machine did not admit, one the sweep's own stop or
a refusal of the run ended, one no worker reached, a skipped one - nothing was observed of those, and
the record keeps what it held; recording wherever `arm.json` is written would let a machine that
stops admitting arms turn every recorded `passed` into `not-admitted`. Never: a self-test, whose
subject has no sink. A leg swept on a host writes no record there - its copy keeps none - but tells
the machine that dispatched it: each verdict, and the tables it names, as notes on the host's
standard error (`NoteSink`, `HostNotes`), lines of the form `host-agent: note <nonce> <kind>
<more|last> <base64>` carrying the text as base64 pieces of at most `NotePiece` bytes - each small
enough that its whole line fits what that machine hands lines on in as it reads the host's standard
error kept as a tail (`ProcessRunner.LongestLine`), so no piece is cut in two on the way - which that
machine reads as they arrive and never relays (`HostAgentLines.Error` hands each whole note to
`RemoteLegRunner.RunAsync`'s `onNote`, fed from `LegRunRequest.HostNotes`), into the same
record-writing sink a leg swept here feeds. So each arm is recorded on the dispatcher as it ends on
the host - a sweep stopped part way, or cut off, keeps what it had judged - and the host's final
answer carries the arms as today and no record material: it travels one way only. A sweep typed by
hand in a copy the harness synced (`HarnessContext.IsSyncedCopy`) - a host's - has no machine to tell
and records nothing (`MutationService.RunAsync`): the record is the main checkout's on the machine
the copy was synced from, and one kept in the copy would hold what that machine never sees and decide
what a later sweep typed there holds. It says so once, as a warning, and names no record; under
`--pending` it holds nothing, and sweeps every selected arm. A leg such a sweep dispatches to a host
still has its notes written there, and they are dropped as they arrive: a note line carrying the
request's own nonce is never relayed, read or not (`HostAgentLines`, given no `onNote`). These
shapes are the host-agent protocol's rise to 10.

A record that cannot be read - not JSON, a member this build does not know, or a hand's damage: a
null where entries or tables go, an entry with a key and nothing it was judged on, a null inside what
an entry was judged on (a target named by no digest, a run-time read named by no hash, a compiler
that is no compiler) - or one a newer build wrote (its `format` above this build's) is never written:
every arm of its leg is pending, a sweep
of that leg says so once as a warning naming the file - delete it to start again, or update the tool
- and once on the leg's line, and records none; so is a record that cannot be written. Either is the
record's, never the arm's: its verdict stands as reached. A sweep typed on this machine names the
record once, in its lines and in its ledger's document (`mutationRecord`), as a listing does.
`clean`, a deleted worktree and a removed
host copy leave the record as it was: it is outside every build directory, worker and copy, and no
part of a key names a tree, a worker or a build directory, so a worker made again after a `clean`
comes to the same key and the recorded `passed` holds. Deleting the record is the one way to forget
it, and `git clean -x` in the main checkout does, as it deletes every ignored file.

`check-mutations --list` (`MutationService.ListAsync`, `MutationListing`) says, without sweeping,
each selected arm's last verdict on each selected leg its S row names, and whether anything it was
judged on that this machine reads has moved since. It reads and verifies the registry exactly as a
sweep does - any problem is the sweep's refusal, every problem listed, so a listing is the door that
validates a registry and stops - resolves `--legs` and `--arms` as a sweep does, reads each leg's
tree once (`ReadSourceAsync`) and each leg's record, and prints a row to every arm on every leg. It
asks no host, takes no lock, begins no run and writes nothing: `CommandRunner` decides per command
line whether a run begins, and a listing begins none. Nor does it need what only a sweep needs -
`reportArgs`, a leg built with Ninja, a selection that drives an arm: a leg that cannot be swept is
said once, above the rows, and every arm on it is pending, so a listing can be asked of any
configuration a sweep would read.

The verdict a row shows is the newest entry nothing read here moved for, else the newest; the
listing compares by the sweep's own rules (`KeyDifference.Listed` beside `KeyDifference.Between`,
sharing every comparison), reading each table by its digest and each file it names in the tree's one
reading, as the tree compares names. An arm is pending for a closed list of reasons
(`PendingReason`), the first that applies said on its row and every one under `--json`: its record
cannot be read, or a newer build wrote it; its leg cannot be swept; it was never swept on the leg;
the verdict shown is no `passed`; it was swept with no key; or the judge's revision, its rows, a
text it cites, a site it mutates, a file of the tree its targets are built from (one gone said as
gone), a file its runner reads at run time, what of the configuration its build, run or judge is
given, or the host its leg names changed. A leg that names no host is read as though swept where
its verdict was judged - which host a sweep would place it on is not read here, so the host is taken
as recorded - and the environment its build is given is the one that host declares now
(`HostsConfig.SettingsFor`), as the sweep that judged it was given what that host declared; a host
the record spells as none declares nothing. Every other arm is *unmoved here*, never said to hold:
a listing reads the rows, the texts, the tree's files and the settings, and never how a target is built, a file the build makes, the
dependency sources it fetched, a file outside the tree it reads, the compilers or the developer
environment - only a sweep on the leg's machine reads those, and nine reasons are a sweep's alone
for that. An arm unmoved here whose build is generated from a file that moved since, or whose
configure would be given something else, says so: the causes kept beside the key name it. A
listing exits `0` whatever is pending - a listing is no gate - and otherwise as a sweep gives it:
`12` for the registry or the configuration, `10` for the command line, `20` where git could not say
what a tree ignores, and `3` where a tree changed while it was read. It is refused with `--pending`,
`--self-test`, `--force-lock`, `--use-staged`, `--here` and the hidden `--held`, each in one sentence:
it sweeps nothing.

`check-mutations --pending` drives, on each leg, only the arms with no recorded `passed` that holds
(`MutationLegRunner`, `ArmHolding`); `--arms` narrows the arms it weighs, and a sweep without it
drives every selected arm, as ever, and records each verdict. Held is decided by one rule, in one
place, on the machine that sweeps the leg: the key of what the arm would be judged on now, read in
full there, is the key of a recorded `passed`. A listing never decides it - it reads no compiler,
build line or file a build makes. So the first worker is made alone - claimed, synced, built whole,
its graph read - and every arm's key is read from its build before any arm is mutated: a worker
between two arms holds what the last mutation's configure made, and nothing could be said of the
leg before the sweep was under way. The sweep is given what the record holds of the arms it drives
(`ArmVerdictRecord.ArmsOf`, `RecordedArms`): every entry of each, and each table the newest names.
A host sweeping one leg of a run is given the same thing for that leg alone, as bytes in a hidden
`--held` of its own command line (`LegRunRequest.RemoteArgumentsOf`, the one way a dispatched leg is
given an argument no other leg has; `--pending` travels among the run's arguments) - refused without
`--here` - and decides held and pending by the one rule there, from its own reading of its copy and
its own worker. A record this machine cannot read, or one a newer build wrote, crosses as that
(`RecordUnread`, read back by its one constructor), and every arm of the leg is pending there for it.
A host given `--pending` and no `--held` holds nothing and sweeps every selected arm.
An arm whose key is a recorded `passed`'s is held: it is not driven, its line is
`skipped-not-selected` - the vocabulary kept, its detail naming the run whose `passed` holds and
when - with `held` beneath it, and the record is told, in one change for every arm held, that this
run verified it (`verified`). Every other arm is pending, its first reason said on its progress
line and as `pending` beneath it, compared with the entry a listing would show - the newest on the
key read now, which is then no `passed`, else the newest - by the comparisons every reader of a key
shares (`KeyDifference.Between`), so the nine reasons a listing cannot give are a sweep's here - the
last, `key-moved`, closing the list where the key is not the entry's and nothing else says how
(`KeyDifference.Closed`): no arm is pending for nothing, and two keys that differ are never said to
be one, a self-test's workers' among them. The
pending arms are queued, the workers they need beyond the first made (`WorkerRoom.Wanted` of their
count), and the sweep goes on as ever, the first worker driving with the graph it read. With nothing
pending, no other worker is made, the first is given back, and the leg reaches `passed` as its own
verdict: it verified each arm, and the tree asks nothing more of it - so "nothing is pending" exits
`0`, and is the proof a round is closed with. The leg's line counts the arms swept by verdict,
those that held and those not selected, and carries `pendingSweep`. A first worker that cannot be
made is the leg's failure, as any worker's is where none is made, and its line says nothing was
verified. The refusal of a selection that drives no arm stands under `--pending`, and a self-test
under it is refused: nothing of a self-test is ever recorded.

### Self-test

`check-mutations --self-test` sweeps the fixture this tool carries, in place of the repository's
registry, which it does not need. The fixture is `tests/mutation-fixture/`, embedded whole into the
tool so every build carries the very fixture its own tests swept: a CMake library, a test binary
that writes its own JUnit report - nothing is fetched to build it - and a registry of eight arms,
one to each verdict an arm's design can reach on any machine: passed, as a TEST-RED arm, as a
BUILD-RED arm, as an arm whose mutation is coupled across two files, and as one whose mutation is
two sites of one file, neither edit building without the other; violated; survived; unattributed;
and failed. It is built as each selected leg builds - its
toolchain, configuration and sanitizer, its developer environment - and each arm is held to the
verdict it is designed to reach: one that reaches it passed, saying so; one that reaches another
verdict an arm's design decides is `violated`, naming both, which is this tool's defect with that
compiler, never the fixture's; and one that reaches no verdict of the judge's - stopped, not
admitted, poisoned - keeps it. The repository's end-to-end tests sweep the same fixture as a
repository's own project, and hold each arm to the same design.

The fixture is written where this tool keeps its own data (see *Heavy legs share a machine*), as
`<user data>/dssharness/mutation-fixture`, each file only where it differs and under a lock every
process on the machine takes: one fixture for every repository on the machine. Its workers are
kept apart from it, in the tree of the leg self-tested, in a family of their own -
`<tree>/.mutations/<key>s-<n>`, a few megabytes each, each a repository of its own holding the
fixture's files, so what git tracks there is the fixture and never the tree around it. Kept beside
the fixture they were every repository's at once, one's toolchain refusing the build directory
another's had made, with nothing to remove them; in the leg's tree they are that repository's, counted by its room,
removed by a `clean` of the leg, and with its worktree or its host's copy. A self-test asks no
host about the room a build of the leg's tree needs, and is placed by none; its workers are
measured where they are planned, and their paths reckoned by the fixture's own longest, in place of
the leg's reserve. It takes the leg's sweep lock, as a sweep of the leg does, and its binary runs
in its worker with what the leg's host gives it, nothing of the leg's tests. A leg on a host is
self-tested there, with the fixture that host's DssHarness carries.

Every worker a self-test makes reads each arm's key from its own build, and the self-test compares
them arm by arm, so each leg's own toolchain proves that two workers come to one key: a key two
workers read otherwise, or one a worker could not read, fails the leg as `violated`, naming the
arm, the workers and the first part that differs (`KeyDifference`); one worker alone says it
compared its keys with no other's. A self-test's keys are on no line: nothing of a self-test is
kept.

## Reporting

Progress is one line per leg transition, not a stream of child process output.
Child output goes to a per-leg log file and is echoed only under `--verbose`. A leg another host
runs has its lines relayed from that host as they come; the host leaves the lines its dispatcher
says for itself - that the legs are starting, where each starts, and each one's verdict - since
said on both, each read twice. So the dispatcher keeps, rather than relays, the line a host's
command ends on where it ended badly, with what follows it: it says how the leg ended itself, by
the leg's line, or in the refusal it raises from what the host said, and a host's summary of its
one leg would otherwise read as the run's own. So too the first line of the host's command that
reads as its own run line: the dispatcher says it once as the leg's, `<leg>: run <id> on <host>`,
and never relays it - one command says one run of its own, not two - where a later line of that
shape is a program's own output, a suite that runs this tool among them, and is relayed as it
came. Such a line is kept with at most 200 lines after
it: a command's failure is the last thing it says and never runs that long, so a line shaped as
one with more after it was printed by the command's own work, and is shown, with what followed
it, as it comes.

**A child's output lives in its log, and nothing else holds it whole.** A consumer's test
started an interpreter with no script to run, which printed the same traceback over and over;
ctest printed all two gigabytes of it, and the harness, which kept every character of a phase's
output in memory, more than once, ended the leg `poisoned` by an `OutOfMemoryException` with 34 GB
of the machine free: no string holds more than about a billion characters. So each line is read
once, as it arrives, for what the phase establishes - whether its success pattern matched, its
timing marks, its last 50 lines, kept in a ring of that many - and written to the log; whatever
reads more of the output reads it back from the log, a line at a time: ninja's last word on a
build, the count a `countPattern` reads, ctest's word that it found no tests, the exception a
failing step printed, the message a run check expects. Of a stream whose caller takes its lines
as they come and asks only for its end - a phase's output, what a host's steps print - the
process runner keeps the last 65,536 characters, for the messages that quote it, and a
line longer than 32,768 characters - a child writing gigabytes with no line feed - is kept in
pieces of at most that many, each a line of the log, cut where no secret the run masks is
parted; a stream its caller reads whole is kept whole, as git's answers are, and a host's
answer, though that too is read a line at a time. The same holds on a machine
that dispatched a leg to a host, whose `--verbose` relays every line the host's steps print: the
dispatcher keeps the end of what it relayed, and the ledger the host answers with.

What is read back is read only from a log that still holds it. A phase closes its child's lines
with an exit line saying how it ended and, to the tick, how long it ran; whatever reads the lines
back first holds the log to its length and to that line, so a log that is gone, cut short or
written again since is said as unread, naming it and why, before any line is handed on - never
read as what is there, which made a build ninja failed read as one stopped from outside, and one
step's lines as another's. Nothing is decided on what is left of such a log: work a verdict hangs
on ends `unmeasured`, saying so - a leg's, or an arm's of a sweep, whose worker goes on - and a
reader that only measures or explains, a test count or ctest having found no test, leaves that
out and keeps the verdict the phase reached.

Every run ends with a per-leg ledger:

```
LEG                   VERDICT        DURATION  DETAIL
win-msvc-release      passed            2m14s  412 tests
wsl-clang-asan        failed            6m02s  3 of 412 tests failed
mac-clang-release     passed           12m40s  412 tests; timings suspect: the host slept
lin-gcc-release       inputs-moved      3m51s  2 inputs changed: config/c.lang.json, ...
vps-arm64-gcc-rel     skipped-unavailable      ssh vps: ssh could not connect
```

Beneath it, `logs:` names where the run's records are, and each leg that kept records names its
own directory and the logs in it; `--json` carries the run's directory as `runDirectory`, at the
top and on such a leg, and the leg's own as `records` and `logs` (see "Where a run's records
live").

## CI legs

`check-ci-legs` reads each leg's verdict from the forge's job metadata, one job at a time and never
from a run's rollup, and tells a test step that failed at or past its time budget - a possible
overrun, whose budget is to be re-derived - from one that failed before reaching it, which is a real
failure. It assumes no workflow of its own. Which jobs are legs, what a leg is called, and which
steps build and test it are the repository's `ci` settings: `legJobPattern`, a regular expression
whose `leg` group names the leg and whose optional `budget` group reads its budget from the job's
name, and `buildStep` and `testStep`, by their exact names. Until they are set, the command refuses
and names them. No forge fixes a leg's job name or a step's, and a command that assumed one
workflow's would read every other as having no legs at all. A leg whose job name gave no budget - a
long name the forge cut short, which a `legJobPattern` must still match, so what follows the leg's
name in it is kept optional - takes it from its workflow's text through `workflowBudgetPattern`,
where `{leg}` stands for the leg's name, and then from `legBudgetMinutes`. A failure with no budget
from any of them is called neither, and counted apart in the summary: a discriminator that invents
its denominator is worse than one that says it has none. A pattern that runs out of time, or a
`budget` group that captures anything but a whole number of minutes, refuses the command as
configuration rather than being read as no match, or as no budget, either of which would let a red
leg pass unseen.

## Exit codes

`0` always means success. "The thing you asked about failed" never shares a code
with "the harness could not run", because the remedies differ.

| Code | Meaning |
|---|---|
| 0 | Success |
| 1-9 | Reserved for per-command meanings (e.g. `verify-git`) |
| 10 | Usage error |
| 11 | Not initialised |
| 12 | Invalid configuration |
| 13 | Refused: precondition not met (dirty tree, lock held, name taken, a host runs a newer DssHarness) |
| 14 | A required tool is missing, or could not be started |
| 15 | A host could not be reached, DssHarness could not run there, or a command run there never reported how it finished |
| 20 | The wrapped command ran and failed |
| 21 | Ran with nothing failing, but a leg reached no verdict or left a set-up it brought up running, or a deletion, a fold, a hand-over, a move of an agent's base or an update of a program stopped part way (not finished within its bound, or its download not reached); it is not a pass, and running it again, once what it names is dealt with, finishes it |
| 70 | The harness itself failed unexpectedly (a defect in the tool) |
| 130 | The run was interrupted before it finished; what it had already done is still reported |

`verify-git` keeps its own contract: `0` success, `1` git not installed,
`2` not a git repository. `legs` and `sync` exit `1` when a leg named with `--legs` cannot run,
or when no selected leg can, and `70` when whether a leg can run was never established, through
a defect in this tool. `build`, `test`, `run` and `clean` exit `1` too when no selected leg can
run and no failure turned one away, each leg's line saying why it cannot; `check-mutations`
exits `21` there (see below), and `check-mutations --list` exits `0` whatever is pending - a
listing is no gate - as `check-mutations --pending` does where nothing is pending. `install-missing-tools` exits `1` when a tool is missing, out of
date or could not be installed, and `15` when a host could not be reached: a tool that is not
there and a host that did not answer call for different things. `check-anchor-balance` and
`check-anchor-citations` exit `1` on a finding, which is what they were asked to look for rather
than a failure of the command. `check-ci-legs` exits `1` when a leg is red and `2` when no job
is a leg - the matrix did not run, or `legJobPattern` matches none of its jobs - an empty answer
indistinguishable from every leg passing, and never read as one. `host-exec` returns the exit code of the command it ran on the host,
unchanged, or 15 when that command never reported how it finished.
A session of a run (`run-orchestrator`, `resume-orchestrator`, `answer-orchestrator`) exits `1`
where it ends paused on a question - a step's, or the budget's at its reserve (`FlowExit.Paused`) -
so a wrapper that retries 21 never loops on a question nobody answered; otherwise `0` done, `20`
failed, `21` failed where every failure that ended it was incomplete after its retries, and `130` at
a safe pause or a stop.
`dssharness help exit-codes` prints the shared table from the code itself; this copy, and the
per-command codes above, are maintained by hand.

Commands that run legs (`build`, `run`, `test`, `check-mutations`) use eight codes from the
range reserved for command contracts, because each calls for a different remedy; 1, 2 and 8
are reached only by a mutation arm, so only `check-mutations` exits with them:

| Code | Verdict | Remedy |
|---|---|---|
| 1 | `violated` | Fix the arm's declaration, or the code it guards |
| 2 | `survived` | Strengthen the test that should have failed |
| 3 | `inputs-moved` or `unmeasured` | Let the tree settle, then run again |
| 4 | `contended` | Wait for the other run |
| 5 | `unwitnessed` | Find out what actually ran |
| 6 | `log-held` | Find out which run still owns this leg's logs |
| 7 | `not-admitted` | Wait for the heavy legs its line names, free memory or room on the filesystem it names, or raise the machine's limits |
| 8 | `unattributed` | Contain the crash or hang in the case, or make the runner write its report |

`failed` reports 20, `refused-locked` 13 and `poisoned` 70; `stopped`, `skipped-unavailable`
and `skipped-tool-missing`, where nothing failed, 21 (`Incomplete`). When legs disagree, the
more fundamental verdict decides the code, in the order given under *Verdict vocabulary*.
Six outcomes therefore carry six codes — refused before starting, the tree moved under the
run, another run in the build directory, another run holding the logs, a machine with no room
for another heavy leg, and a zero exit code with no witness — and a mutation sweep's three
findings three more, because a reader who cannot tell which fired cannot pick the remedy.

Within one command no code has two meanings. A sweep none of whose selected legs can run
therefore exits `21`, incomplete - which is what such a run is, nothing having failed and no leg
having reached a verdict - and never the `1` a build, a test and a run give there: of a sweep `1`
is an arm `violated`, and a caller sorting by exit code would be sent to fix an arm's declaration
by a sweep that drove none. A test holds every leg-running command to it.

## Success witnesses

A zero exit code is not proof a command ran. A test invocation must declare a
`successPattern`, and a runner phase may; where one is declared, the work passes only
if the exit code is zero **and** the pattern matches the command's own output. This
exists because a wrapper that reports success without evidence is indistinguishable
from one that never ran, and it was measured happening three separate ways: a suite
that printed `failed=0` while exiting 2, an exit code read after a pipe, and a test
command that exited 0 having run no tests at all. An emulator's witness applies the same
rule to the emulator itself, read its own way: the witness must exit 0 within 60 seconds, and
its pattern is matched against all it printed at once, each line as it reads whatever ended
it, within one second - an emulator whose witness fails any of that is unavailable, saying
which. A phase's pattern is matched against each line as the log keeps it,
whatever ended the line: a Windows program ends its lines with CRLF, and a `$` that matched
before the line feed left the carriage return between the text and the end, so a pattern that
passed on Linux and macOS never matched on a Windows leg. It is matched against one line at a
time, as each arrives, so one reaching across a line break - `\s+` between what two lines
print - matches nothing, and a line longer than 32,768 characters is matched in the pieces its
log keeps it in. A timing pattern is read the same way, and keeps its first 10,000 marks in a
phase, saying so where there were more: a child printing a mark on every line of a flood would
otherwise have the flood held again, as marks. A phase's pattern that cannot be evaluated
against a line in five seconds is not evidence either way: a success pattern that cannot is
refused, naming the pattern, once its command has ended, and a timing pattern that cannot leaves
the phase unmeasured by it, saying so.

## Timeouts

Wall-clock timeouts are not used **for phases and legs**, save the one below: a time budget is a
guess about workload size, and honest runs exceeding it get killed. Where a bound is
needed, a phase declares a **stall** bound instead — no output for N seconds means
hung — because output cadence stays stable even when total duration is not.

`IProcessRunner` does support a per-process budget, for a probe that must not hang: the
probes that measure a host, and an emulator's witness, each have one.

A mutation arm's run is the one whole-duration bound a phase gets, and it is not a guess: a
mutation that turns a loop endless can go on printing for ever, which no stall bound catches, and
the unmutated run of the very same binary, measured minutes before on the same machine, says how
long it takes when nothing is wrong. So a mutated run may take `mutations.runTimeFactor` (10) times that run, never less than it
and a minute, and one stopped past that is `unattributed`, saying so and which of the two set its
bound: a binary whose unmutated run is short is stopped at that run and a minute, never said to have
run past ten times it. A stall bound applies to it as
to every phase. Each such run, and the unmutated one that bounds it, is timed on both clocks as
every phase is, by the sweep itself: one that spanned a clock step or a host sleep is said on the
leg's line, among why its timings are suspect, however it ended - stopped past its bound included,
which no phase is left to say - with what each of the sweep's builds says of its own, a worker's
rebuilt from clean among them. A timing mark changes no verdict.
