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
`fold-agent`, `delete-agent`), `check-root-litter`, the anchor commands (`write-anchor`, `set-anchor`,
`read-anchor`, `read-anchors`, `check-anchor-balance`, `check-anchor-citations`),
`fix-line-endings`, `check-ci-legs`, `legs`, `host-exec`, `install-missing-tools`,
`sync`, `build`, `test`, `run`, `check-mutations`, `clean` and `help`.

Every section of this document now describes code that exists. Where a rule is stated in
the present tense it is enforced, and a gap between the two is a defect in the tool rather
than a section still waiting to be written.

## Layering

```
RepoHarness.Cli        Program.cs: argument parsing and dependency wiring only
RepoHarness.Core       domain, services, abstractions
repo-harness-test      tests

(RepoHarness.Adapters was planned for the build adapters; they arrived in
RepoHarness.Core instead, beside the build service that is their only caller, and the
project was not created.)
```

`Core` is a library, so "no logic in Program.cs" is enforced by the assembly
boundary rather than by discipline.

### The platform layer

`HostPlatform`, `FilePermissionsFactory` and `ProcessTableFactory` are the **only**
types that observe the operating system. Everything else depends on `IHostPlatform`,
`IFilePermissions`, `IProcessTable`, `IProcessRunner` and `IFileSystem`.

A platform-specific implementation is created only where behaviour genuinely
differs. Today that is two families of behaviour.

**File permissions**: making a file readable only by its owner, deciding whether a file
is a program at all, and whether other users can read or change a file. Unix answers
these with mode bits; Windows answers them with access lists and file extensions. Hence
a POSIX implementation and a Windows implementation, and no third.

**The process table**: what else is running on this machine, with each process's parent,
its start time and its command line, which is what a leg's contention check reads.
Windows publishes it through WMI, Linux through `/proc`, macOS through `ps`. Behind the
seam so that nothing above it branches on the operating system to find out, and so a test
can say what the machine was running without the machine having to be running it.

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
programs in. Left to the runtime, it would be looked for beside the running executable and in
the current directory first, and the current directory is usually the repository, so a file
committed there under a tool's name would run in place of the tool. On Windows a name
without an extension starts only `<name>.exe`, never a batch file, whose arguments cmd.exe
would parse a second time, and a path without one starts that path with `.exe` added. A
program a leg's configuration names by a relative path is read from the directory its phase
starts in - a step's working directory, the test runner's - never from wherever the harness was
started: for a leg on a worktree those are different copies of the same file.

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
  and nothing ever read - each leg builds and tests the one config its `config` names.
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
orchestrators directory, `.orchestrators`, and for the harness directories a person fills by hand
— `sshItems`, `wslDistros`, `runner/.env`, `runner/.secrets` — so a clone arrives with each
directory in place. A configuration `init` writes names `.worktrees` as the root; one that names
none keeps `.harness-config/worktrees`, so worktrees made before stay where they are. The root
was ignored whole, with no placeholder, and the costs of this shape were measured then and are
accepted now: a directory holding a tracked file never reads as ignored itself, so `git
check-ignore <root>` answers *not ignored* while every worktree inside it is ignored, and the root
can no longer be kept on another disk through a link, which git would list as an untracked entry
and keep nothing behind; `init` names such a link and writes nothing through it. Sync does not
depend on either answer: it withholds the configured root and `.orchestrators` by name, whatever
git says, and its guard against a withheld path that stopped being ignored takes the root's
placeholder, untracked until it is committed, as the harness's own - any other untracked file
there still refuses, named as the worktrees root it is. The root may not be `.git` or
`.orchestrators`, or inside either.

`init` leaves hand-written `.gitignore` rules alone, so a repository that already ignored one of
these paths by hand keeps its rule beside the managed one. What `init` reports is git's own answer,
never a reading of how the rules are spelled. Each managed rule carries a path it decides - the
file it names, or a name inside the directory it rules on - and `git check-ignore -v --no-index`
says which rule decides each of those paths in the tree. A rule deciding one against the block is
named as the rule git follows: it undoes the block there, whether it is a re-include after the
block, a nested `.gitignore`, or a whole-directory rule such as `.env` - which takes the
`runner/.env` directory, from which git re-includes no placeholder. The same paths are then asked of
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
git never looks past, ends a whole `check-ignore` with exit 128 - is asked again on its own, named
with git's reason, and every other path is still answered. A scratch repository that cannot be
written is a note that git could not be asked; one that cannot be removed afterwards is a warning,
and the answer stands.

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
is cleared first and the requested one set after. An answer that does not name `GIT_DIR` is not
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
- **A mutation worker kept.** The workers kept beside the worktree (see *Mutation testing*) go
  before it, once every check above has passed, and are asked about before any goes: one a sweep
  still running holds keeps the worktree, exit 13, and one that cannot be removed, or told for the
  harness's - its marker cannot be read - keeps it as a failure, exit 20, as workers that cannot be
  looked for do; nothing is removed. A sweep that takes one between the asking and the removal keeps
  the worktree as well, and so does one whose removal then fails, the others gone by then, which is
  said. An interruption as they go says which went and that the worktree is whole, exit 130.
  `--force` deletes the worktree and leaves a worker that is held or could not be removed, saying
  so, and deleting the worktree again removes it once nothing keeps it.

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

An agent's worktree is `<worktrees.root>/<orchestrator>/<agent>`, addressed `orchestrator/agent` by list-worktree
and delete-worktree, its host copies named `orchestrator--agent` (`WorktreeAddress`). The directory named for an
orchestrator under the root is shared with plain worktrees' names, so create-worktree refuses an orchestrator's
name and create-orchestrator a worktree's, and delete-worktree never deletes a directory holding worktrees below
it, forced or not: below the directory named for an orchestrator, every directory with a `.git` of its own
counts - but for a mutation worker kept beside an agent's worktree, a copy of it, which goes with its agent -
and below any other directory with none of its own only what git records - a husk's submodules are its own contents,
which `--force` deletes with it. Where git cannot list its worktrees, nothing is deleted. delete-orchestrator removes
every record last - each agent's after the rest of that agent, the orchestrator's after its agents - so a removal that
stops part way (exit 21) leaves only records that read, and run again it finishes. list-orchestrator counts an
orchestrator's open agents as create-agent does (`OrchestrationRules.OpenAgents`), and says where each agent's
worktree is from its own record, the worktrees root it was made under.

### Seeding and folding

The seed is the main tree's uncommitted state handed to the agent when it is made - every path git status lists,
less `TreeFloor`: `.git`, `.orchestrators` and the worktrees root, asserted where the copying happens rather than left
to ignore rules a repository can edit away - each changed file copied into its worktree, with the copy's SHA-256 and,
where the platform has one, its execute bit, and each deletion made there too and recorded as absent. `seed.json` is
then what the agent shares with the main tree, path by path. An untracked directory git will not look into - a
repository of its own - is named and not handed, since a fold never moves one. A symbolic link is refused rather than
handed over as the file it leads to, before the worktree is made, so the refusal leaves nothing behind. seed-agent
counts as the agent's own only a change of what it shares that it made itself. What the main tree holds later is
weighed against what the agent shares (`AgentFold.MovedAsync`), never against git status alone, which forgets a path
the main tree commits or puts back as its HEAD holds it: a path the agent was handed or folded is stale once the main
tree's copy is not the one its seed records, and any other once the main tree's copy - committed since or not - is not
what the agent's base holds, asked of git as a fold asks it. refresh-agent hands over every such path under the paths
it is given, refused, copying nothing, where the agent changed or deleted one of them (`EditedAsync`, the same
comparison asked of the agent's worktree), and seed-agent hands them besides the main tree's uncommitted state. A
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
directory where a handed file goes that the hand-over does not delete. Neither hands anything while a move of the
agent's base stands stopped part way, or its HEAD is off its base. Both say when the agent's base is not the main
tree's HEAD, seed-agent --empty too. A path named otherwise than in UTF-8, committed or not, refuses any hand-over,
named as git's quoting writes it: no file opens here by such a name.

rebase-agent moves an agent's base to the main tree's HEAD (`AgentFold.MeasureRebaseAsync`). Each path the two commits
hold differently (`git diff --name-only`, commit to commit) is shared - kept as its seed records it, since the seed,
not the base, is what a shared path is weighed against - or held as the new base holds it already, or held as the old
base holds it, and comes in as git holds it (`git checkout --no-overlay <commit>`, the paths on standard input and read
literally), or changed by the agent - asked of git against both commits, with anything where the old base holds
nothing its own, untracked, ignored or staged, and one holding the new base's bytes included - which refuses the move
unless `--settled` names it. What each commit holds at a path is told apart (`HeldAtAsync`): a directory the old base
held, now a file or a submodule's entry, is held as the old base holds it while the agent's worktree holds a directory
or nothing there, and a submodule's entry as git compares it, so neither is taken for something the agent made.
Nothing of the agent's is written over or hidden: git removes a file or a link where a path it writes needs a
directory, removes a directory, with all it holds, where it writes a file, and hides all a directory holds where it
writes a submodule's entry the old base did not hold - a submodule's own checkout it leaves alone - so each refuses the
move unless it is the old base's own, coming in with the rest - the agent's own, settled or shared, each named with
what to do. A file or a submodule's entry where the old base held a directory is written last
(`RebasePlan.WrittenLast`), once what was below it is: written first, git removes that, with the directories it leaves
empty, where asked for both at once it finds that gone and stops, and a submodule's entry written first leaves it in
place, hidden from git. The record names where the move goes (`Moving`) before anything is written, then the new
base's paths are written, HEAD and the index move (`git reset --mixed`), then the record and the worktree's base ref
name the new base: a move that stopped part way, exit 21, is finished where it was going by running rebase-agent
again - whatever the main tree committed since, and with what it wrote held as the new base holds it, a file the old
base lacked included - and fold-agent, delete-agent, seed-agent and refresh-agent refuse the agent until then, saying
so. Only a move its record names is ever finished, and only from its base or where it goes: a HEAD anywhere else was
moved by hand - back, forward or beside its base, a move under way or not - or names no commit, and each command
refuses it, saying how to put it back; one past its base that the main tree's history does not hold is a commit made
inside the agent. Where git cannot say which a HEAD is, each fails, exit 20, saying so, never guessing.

An agent's contribution is a measurement (`AgentFold`): its worktree's status, and every path it shares with the
main tree whether its status lists it or not, less the shared paths left as they were. Each path goes in exactly one
list - its own, deleted, inherited, already in the main tree, settled - or is refused, and one refusal refuses the whole
fold before anything is written. A shared path is compared with what both trees held - a file, or its absence; any
other with the blob at the agent's own base (`cat-file --batch-check`, one process), never the main tree's HEAD, which a
sibling's committed fold moves, and the main tree is asked whether it moved from that base as git status would answer
(`git diff --name-only` against the base, one process): through the index's line-ending rules, so a file only checked
out with other line endings is no change, and with its mode, so a sibling's changed execute bit is one. A fold records
what it wrote, removed and found already in as shared, so a later fold - after a review sends the agent back - weighs
those paths against what the fold left, never against the base: the agent putting a path back as it was is its change
to fold. A refusal of a changed main-tree path says whether a commit or an uncommitted edit changed it, since the two
are reconciled differently. A deletion needs the same baseline proof a copy does. A directory with no `.git` of its own
holds no file at its path, so a file the agent turned into a directory folds as the file's deletion and the directory's
files, and a directory it turned into a file as the files' deletions and the file: every removal is made first, with
the directories it empties, then every file written. A path reached through a link in either tree, a repository of
its own - a submodule, or the agent's own - what the main tree holds in the way of the agent's files (a file where one
needs a directory, or files the agent did not delete in a directory one replaces), a HEAD off the base and a move of
the base stopped part way are refused, and so is an anchor registry the agent changed as a file: its rows go in through its rows directory, weighed
against the registry the fold would otherwise have written over. `--settled` is the one way out of a path's refusal, asked before the deletion branch so a
deletion can be settled too; a settled path the fold does not weigh is refused as a misspelling. A path the main
tree already holds as the agent does is already in, so a fold run again finds its own writes. A path this process
cannot look at is never read as absent, which would take the agent's file for its deletion: the fold fails before
anything is written. A file of either tree that changed after it was weighed is never written over: the agent's copy is
refused where it no longer holds the SHA-256 weighed (`VerifiedFileCopy`), and the main tree's file is weighed again
before each write and each removal. The fold stops there, exit 21, for a run again to weigh it anew.

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
then (`applied-rows.json`): one the agent declares as it did then is never applied again, so a change the registries
took since - the orchestrator closing the row, a sibling's cross-reference - stands; one it declares anew over it is
refused where the registries no longer hold what the fold applied (`DifferencesAsync`, which holds a row to no rule a
write is held to: one applied before a rule it breaks is still the row that was applied), as a file the main tree
changed is.

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
question it cannot answer is named as that, never read as nothing left. An agent made under a worktrees root the
configuration no longer names is refused with nothing touched, since delete-worktree looks under the root the
configuration names.

A closed agent is never folded, seeded or refreshed again. delete-agent run again compares its worktree with what
closing recorded, never with the main tree, which later agents go on changing: a file changed or new since is
work, left for a person (exit 21); a file gone since is the debris of a removal that stopped part way; a path the
main tree ignores is never work, asked of the main tree with its index, since the removal may have deleted the
worktree's own ignore rules first. A directory with no `.git` of its own is never read - git would answer for the
main checkout - and never forced: its evidence is kept again, and the forced removal is named for a person. A
worktree made at the path since is another, and not the agent's to remove.

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
| **Tree** | Which checkout am I acting on? | main checkout, a worktree, a host's copy |
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
  ignores, so it is refused too. Both are checked before connecting, and reported with the
  command that fixes them. Per-host files also confine that risk: one world-writable shared
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
- **No quoting.** An ssh server hands its command line to a shell, and which shell is not
  known in advance: sh, bash, zsh, fish, cmd or PowerShell. The harness quotes for none of
  them. The command line holds only words every one of them reads literally (letters,
  digits and `._-/=:+`), and anything else travels on standard input. Which shell answers is
  measured once per connection, by whether `echo %COMSPEC%` comes back expanded, because cmd
  needs backslashes in the path of a program.

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

Both ends must be the same build, so before anything runs on a host:

- A host without DssHarness has this machine's version installed.
- A host that is behind is updated to this machine's version. It is never downgraded, and
  not updated while DssHarness is running there: an update replaces a running tool's
  files underneath it on Linux and macOS, and fails part way on Windows.
- A host that is ahead stops everything (exit 13) until this machine is updated, with the
  command that updates it. Moving the host down would undo somebody else's update.
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
- **The paths in its ledger**: `runDirectory`, a leg's `detail` and `space`, what a timing
  pattern matched, and the instance a developer environment was set up from. The machine that
  asked prints that `runDirectory` in its `logs of <leg> on <host>:` line. A survey's answer
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
  and beneath a ledger's table, the output a command relays, and what an emulator's witness
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

- **`--dry-run` installs nothing.** It reaches and asks every host exactly as a run does, and
  reports each tool it would install or update as `would install` or `would update`, with the
  command that would run, `sudo` and all. Nobody is asked for a password, and no host is asked
  whether one is needed: a dry run that stopped for a password would be the install it says it
  is not. It exits `1` while anything is missing, and `--json` carries `dryRun`.

- **The .NET SDK is the default tool on every remote leg.** A WSL distribution or ssh host that
  cannot run DssHarness has it installed, under the home directory, where no login-free PATH
  names it — which is why every command the harness runs there spells the resolved absolute path.
  The machine running the harness is assumed to have it already.
- **Everything under `tools` that carries an `install` is probed and installed or updated
  through it**, by `probe.args`, `probe.regex` and `minVersion`. An entry with no `install` is an
  allowlist entry: probed where it declares a probe, reported when missing, never installed. That
  is how a program shipping with the platform, or with the repository, is allowed to appear in a
  runner's steps.
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
  environment sets up for its processor, as the run looks before the leg starts: set up on this
  machine for the look, searched, then each directory searched for programs. Found there it is
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
    could not be looked at or set up leaves a tool the host's own `PATH` lacks unknown, saying why.
  - **Another host's is set up by the DssHarness that runs its legs there**, and this command
    reaches that host through its shell alone: a tool its own `PATH` lacks is unknown for a leg in a
    developer environment, never missing, and nothing is installed for it - a second copy of a tool
    the leg finds is exactly what this command must never install.
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
| `inputs-moved` | Files the tests read changed while they ran; or, for a leg on another machine, a file its host's copy needed changed or was removed after the run began, before it was carried there, and nothing of the leg ran | **yes** |
| `unmeasured` | Whether those files held still could not be established; or what a phase printed could not be read back from its log where a verdict was to be read from it; or ninja's log could not be read around a mutation arm's build, so nothing witnessed what it rebuilt; or the report a mutation arm's run wrote, or its binary's unmutated run, could not be read from its file | **yes** |
| `contended` | Another process used the leg's build directory while it ran | **yes** |
| `skipped-not-selected` | Filtered out by `--legs`; or a mutation arm `--arms`, or its S row, leaves out of the leg, and a leg whose sweep drives no arm | no |
| `skipped-unavailable` | No host can take the leg; its host or its tree could not be reached; whether a program it starts is there could not be established; git could not answer in its tree; or no worker of its sweep fits the room left or the path limit, and every arm of the leg with it | warning |
| `skipped-tool-missing` | A required tool is not installed | warning |
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
report: those that did no work, and those stopped before finishing. The verdict table
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
the command (`HostAgentRequest.RunId`, protocol 7), so the legs one command sends there wait for each
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
  merged over `all`. It is matched against the runner's own output and never against
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
- A leg-running command asked for `--json` answers with its ledger whatever ended it, once its
  command line was read: a refusal before any leg was placed, a selection no host could take, a
  log another run holds, a refusal while the legs ran - each is the document too, with the code
  the process exits with and the line it ends on. An interruption is said as one: `cancelled`,
  with exit 130, so a script never reads it as red. A machine that dispatched a leg reads the
  host's answer this way, so a host's refusal arrives as that refusal, in the host's words.
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

The watch goes only as deep as the inputs: each directory holding one - the root among
them - is watched for its own files only. A directory tracked for a placeholder, as init
keeps the worktrees root and `.orchestrators`, costs one shallow watch, and other trees
building below it are never heard. Measured: watched all the way down, an agent's build
below the worktrees root overflowed the operating system's buffer, and a run in the main
tree that only read its own inputs ended `unmeasured`. Past 64 watches, the top-level
directories holding the most directories of inputs are watched all the way down instead,
until the rest fit, and a tree whose inputs lie in more than 64 top-level directories -
the root counted among them where it holds one itself - is watched whole, other trees'
builds below the worktrees root heard with the rest.

A leg on another machine tests that machine's copy, which holds still under it; what can
move is the tree here, before the copy is made of it. So a run reads each tree its hosts'
copies are made of once, as it begins and before any leg's work, and every copy is made from
that reading, as [Syncing a tree](#syncing-a-tree) says: an edit put back before a host's
sync comes round leaves no trace there, and where a file to be carried no longer holds what
was read - edited, or removed - the legs on that copy are `inputs-moved`, naming it, with
nothing of them run, while the legs on other copies still report. A file that goes while the
tree is being read does the same to every copy of it. Measured: an edit a mutation-testing
tool left in the tree for 7.3 seconds of a run, and put back, was built and tested on a Mac
and reported `failed`, while the tree was the same at the run's start and end. A run with
`--use-staged` reads nothing, and tests whatever the copies hold. A leg on this machine is
held to its own guards instead, which watch its inputs while it builds and while it tests: an
edit made after the run began and left in place reaches a leg here that starts after it, which
builds and tests the edited tree from start to end, never a mix of two - so one run's legs
here and on hosts can have tested different trees.

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
- What sampling cannot see is stated in the report: a tool started from inside the
  build directory with a relative path, since another process's working directory
  cannot be read, and processes that do not expose their command line.
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
  first the mutation workers kept beside it, asked about before any goes, as beside the worktree
  itself: one a sweep still running there holds keeps the copy, still recorded, as does one that
  cannot be removed or told, and a copy kept once some had gone says which went (see *Mutation
  testing*). A host is
  reached through the worktree's own configuration, read before it goes - its branch may declare a
  host the configuration the command runs in does not - or else through that one; a host neither
  declares is not asked, and its copy is forgotten, named. Each copy is removed under the lock a leg
  this machine runs there takes, the record read again once it is held, so a copy another worktree
  of the name has claimed since is left for it; it is forgotten before that lock is let go, and its
  marker goes last, so a removal that stops part way leaves the rest marked as the harness's. The
  host is asked from its home directory, which is there when the directory the copy was kept in is
  not, so a copy whose directory is gone is answered as not there, and forgotten.
  A copy that cannot be removed then stays recorded, and the deletion fails naming it though the
  worktree is gone, with the highest code a copy was left with - 13 where a run holds one or it was
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
   appended - and held to the whole path CMake cached. A name compared with a name let a
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
their key exclusively instead - spelt as one more copy beside the tree, which no copy is - so
it meets another sweep of the leg's variant and a `clean` of its workers, and never a build,
test or sync of the leg (see *Mutation testing*).

- A held lock **refuses immediately**. It never waits: silently blocking for
  hours is worse than a refusal that names the holder.
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
refuses never started, and names none. A leg another host ran was run there under a run of its own, and its
line names that host's directory, as `logs of <leg> on <host>: <directory>` and as the leg's own
`runDirectory`, with the host's home written as `~` (see "A host's home is `~`").

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
  `space`. A leg on a host is asked of the DssHarness there, in the copy - once the copy is known
  to be there, so a tree never synced to a host has nothing removed rather than a host refusing it.
- **A leg's mutation workers go with it.** The workers its sweeps keep beside its tree are removed
  the same way, and first, so the room its line says is the room once they are gone: its own
  variant's, its self-test's, and those of a variant no leg of that host and tree builds any more,
  which nothing else would ever remove - another leg's are that leg's. Each variant's go under the
  lock a sweep of it takes rather than its build's, and are said on the leg's line with what they
  held, which `--json` carries as `space.workerBytes` - what was removed of them, 0 where every one
  there was kept, or in a dry run what is there - wherever anything named as a worker of the leg's
  was found: a leg whose build directory was not measured, a link, locked, not removable or on a
  host holding no copy of the tree, carries a `space` saying its workers alone. A worker a live
  sweep still claims is kept, the leg `refused-locked`, as is one whose claim cannot be read; one
  that cannot be removed fails the leg, whose line still says what went, and counts it; one whose
  sweep died holding it is released first, and said; and a directory under a worker's name that no
  sync made is said and left. Where a host holds no copy of the tree, nothing there can run a
  clean, and the host is asked instead to remove the workers left beside where the copy was (see
  *Mutation testing*): one a sweep holds there leaves the leg `refused-locked`, one that could not
  be removed `failed`, and a removal that was stopped `stopped`.

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
  one: a consumer's leg, placed where neither its own tree's copy nor the main checkout's had built
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
- **A sweep fills its first worker.** A sweep of a leg's mutation arms builds in workers beside
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
- **A copy a sync is writing is marked unfinished**, before its first write or deletion - or its
  configuration, where that changes - and complete once it is verified, as a takeover is marked
  begun and finished. A sync that stops part way - a tree that moved, a connection that dropped, a
  verification that failed - leaves part of one tree and part of another, which no run began with.
  Marked so, the legs of a run with `--use-staged` on that copy are `inputs-moved`, read by the
  machine that would have synced it, and `sync --artifact` carries nothing into it, until a sync
  finishes it. The next ordinary sync, which plans from what the copy holds, puts it right; marking
  a copy again keeps when, and by which machine, it was made. The mark is written into the marker
  only while it holds, so a copy every sync finished carries the marker a build from before it
  reads; such a build refuses an unfinished one as unreadable, and the remedy is to upgrade, never
  to delete the marker, which would turn the copy into one taken over.
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
  would take it.
- **It says what that is about to cost before it starts** — not only in the refusal, because
  somebody who reads the flag in the help and types it never sees a refusal.
- **The marker records which it was.** Afterwards a copy taken over and one the tool made are the
  same directory, and only one of them deleted somebody's files; the marker is the only thing left
  that can say so.
- **What survives an adoption is narrower than it looks.** Its `.git` and so every commit in it,
  the rest of `.harness-config`, the worktrees root and whatever `sync.neverTransfer` names are
  protected from the deletion — though `config.json` there is replaced with this tree's, which the
  refusal says. The ignore list is *not* read from that host: it is this tree's, listed by asking
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
- **`--dry-run` shows the plan instead of refusing,** for a directory the tool did not create and
  for one whose deletions are over the bound. A preview changes nothing, so there is nothing for
  either refusal to protect — and the bound's own message says to run with `--dry-run` to see the
  list it was until now refusing to show.
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
  said, by a build and by a step that asked for its inputs held still. The copy's history stays its
  own.
- **Content, never timestamps.** A file is written only when its content differs. An unchanged
  file is not touched, so its modification time does not move and an incremental build on that
  host stays correct; a changed file is rewritten now, so its time advances. Nothing compares two
  times taken at different moments or on different machines.
- **Deletions propagate.** A file removed from the source is removed from the copy in the same
  sync. A copy that only ever gains files is not a copy of the tree: a deleted source file keeps
  compiling, a deleted test keeps running, and a renamed file exists twice, so the leg's verdict
  describes a tree that no longer exists.
- **The never-transfer floor is also a never-delete floor.** `.git`, `.harness-config`, the
  worktrees root and everything under `sync.neverTransfer` are neither written nor deleted. A
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
  there as it works, each run's records, the record of host copies and each action's own `build`
  and `artifacts`: the run state of whichever machine made it, which no sync carries and no one
  edits by hand, where a name neither counts nor is looked for below. Counted, a fresh worktree's
  first run, its own `build` not made yet, was told the `build` entry `init` writes protected
  nothing, having found an action's working space, and to write `**/build`, which would withhold
  every source directory of that name too. A search that still runs out says so; it never reads as
  having found none.
- **The copy gets `.harness-config/config.json`, and nothing else from that directory.** A leg
  placed on a host runs DssHarness there, and DssHarness in a directory holding no configuration
  refuses as not initialised — so without it the copy is a tree no leg can run in. The rest of the
  directory is connection data, credentials, locks and logs, each local to a machine by design.
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
  the host's tree for them.

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
that stopped part way, or failed its verification; a copy so marked makes its legs `inputs-moved`.

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
  directory of files. A value that came from `.secrets` never reaches a log, an argument list or
  an error message.
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
- **The window is the failing unit's own** — its output up to its verdict line — never a
  once-per-run sample. A failure is excused only when at least `minStepsInFailureWindow` steps of
  at least `minStepSeconds` fall inside that window. A once-per-run sample was measured charging
  genuine-looking failures to the tool under test on a loaded machine and excusing them on a quiet
  one, on the same day; a sample taken before a run says only what the machine was doing then.

## Mutation testing

`check-mutations` is the mechanical proof that a repository's tests can fail. For every selected
leg and every arm its registry declares, it mutates exactly the text the arm names, builds the
arm's target, proves every object that depends on the site was rebuilt, runs the arm's test binary
whole, judges what reddened against the declaration as an exact set, and puts the site back,
checked by its hash. A BUILD-RED arm must instead stop the build at an object that depends on its
site, and its paired positive control must build. All of it happens in worker copies of the leg's
tree, never in the tree itself, which is only ever read. A leg's verdict is the worst of its own -
its workers' builds and its test binaries' controls - and its arms' (see *Verdict vocabulary*).

### The registry

`mutations.registry` names the repository's own file, one row to a line, its fields separated by
`|` and trimmed, a line whose first character that is not blank is `#` a comment, nothing escaped
and the last field taking the rest of the line. An A row declares an arm - its site, the files
holding its before- and after-text, its red kind, its target and runner, its case count and its
diagnostic - and C, G, B, M and S rows, each following the A row of its arm, add a case that must
redden, a neighbour that must run and stay green, a BUILD-RED arm's paired control, another site
mutated with it - another file, as the tree's own file system compares names - and the legs it
runs on. A
text is a file in `mutations.textDirectory`, read as it is held, less a UTF-8 byte order mark at
its start and one line ending at its end, and given the site's line endings where the site ends its
lines otherwise, so one registry serves a checkout with either. Every file directly in that
directory must be cited by some row; what lies below it is not listed, and a file there that a sync
withholds - one git ignores, as a desktop's or an editor's leftover is, or one the configuration
keeps from a sync - is no text: no copy of the tree holds it, so nobody could drive it.

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
it leaves out, or whose S row leaves the leg out, is `skipped-not-selected` on that leg. An arm
selected whose S row names no selected leg is warned of before the sweep starts; and a selection
whose every arm is such a one is refused, exit 10, naming each and where it runs, before any host
is touched: every leg would be skipped, and the run would pass having swept nothing. A host sweeping
one leg of a run refuses nothing of the kind, since only the machine that selected the legs sees
them all.

### Workers

A leg's workers are copies of its tree kept beside it, `<tree>.mutation-<key>w-<n>`, a family
of copies of their own. The key is the first 7 hexadecimal digits of the SHA-256 of the leg's
variant, as its build directory is named, so the machine that dispatches a leg and the host that
sweeps it name its workers alike - and a key, never the variant's name. A worker is a second tree,
built as deep as the first, so where paths are bounded every character its name adds is one the
tree's own path must leave free; spelt out, a variant's name stood in a worker's path twice, in
its name and in the build directory within it, and a repository whose path budget is reckoned to
the character had room for neither. A worker's name adds twenty characters whatever its variant is
called, and the lines of a sweep and of a `clean` say which leg a worker is. Two variants of one
tree whose names came to one key would share their workers and their lock, and neither would be
judged on the other's: each sweep syncs a worker again by content and builds in its own variant's
directory within it. No worktree's copy is named into the family, since a copy's name holds no dot; and
a worker kept beside a worktree's copy on a host, `<repositoryPath>.worktree-<name>.mutation-...`,
is spelt as no copy's name, so a listing of the host's worktree copies never holds it. Nor is a
worker kept beside a worktree taken for a worktree, though it holds a repository of its own:
`list-worktree` passes over it, and it is no worktree below an orchestrator's directory. A sweep
runs `mutations.workers` of them (2),
never more than the leg has arms to drive, and one in a WSL distribution, whose sweep this machine
admits whole. The tree is read once, as a sync reads it, and every worker is synced from that one
reading, so every arm measures the same tree however long the sweep takes, and an edit made
meanwhile reaches none of them. A worker stays between sweeps and is synced again by content - so
its build directory stays warm, and a site a killed sweep left mutated is put back - and built
whole as the leg builds before it drives an arm. A worker is claimed while
a sweep uses it, in `<worker>.claim.json` beside it, by the claim a run takes on its log directory,
which never makes the copy it claims: the sync that makes a copy refuses a directory it did not
make. A claim whose sweep died is released and said; the copy it held is synced again, as every
worker is before it drives an arm.

A worker needs its copy of the tree and what a build of the variant comes to - the leg's
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

The workers go with their tree, since nothing would remove them once it is gone. `clean` removes a
leg's with its build directory (see *Disk space*). `delete-worktree`, and so `delete-agent`,
removes the workers kept beside the worktree before the worktree itself, and each host removes
those kept beside its copy before the copy (`remove-workers`, served as every sync operation is):
each worker a sync made, of whichever variant, with what an unfinished removal left aside - only
what is named as a worker's, since a name is all that is left to tell one by. Each worker answers
for itself: one that cannot be removed is left, saying why, and the others still go; one gone by
the time it is reached was somebody else's to count; and a removal that is stopped answers with
what it had done. A worker a sweep still running holds says its tree is in use: the worktree is
not deleted, nothing of it removed, and a host's copy stays, still recorded - and one that cannot
be removed, or told for the harness's, keeps its tree as well, as a failure rather than a tree in
use - or `--force` deletes the worktree and leaves that worker, said, which deleting the worktree
again removes once nothing keeps it, as it removes whatever a worktree already gone left. A claim a
sweep took while its worker was being removed is that sweep's, and stays. A worktree whose own
removal then fails, or is stopped, says which workers went before it. `list-worktree` says a
worker whose worktree is gone, with the `delete-worktree` that removes it; `delete-agent` removes
those left beside an agent whose worktree is gone already, and leaves the agent undeleted where
they cannot be looked for; and `delete-orchestrator` takes those left beside agents that are gone,
refusing where they cannot be looked for and, once the orchestrator is deleted, saying beside the
deletion whatever kept any - interrupted as they go, it names each agent whose workers are still
to be removed.

### Fetched sources

A worker builds the dependency sources the leg's own build fetched: each `FETCHCONTENT_SOURCE_DIR_<NAME>`
entry of the leg's CMake cache whose sources are there - in `<FETCHCONTENT_BASE_DIR>/<name>-src`,
or where the entry names - and with every declared dependency found, fetching is turned off, so a
worker the network cannot reach still configures. One not found is fetched by the worker as the leg
would fetch it.

They are carried into each worker rather than pointed at where they are. They sit in the leg's own
build directory, which the sweep's lock deliberately leaves to a `clean` or a `build` of the leg:
pointed at in place, a clean mid-sweep failed every later arm's build, and a build that fetched
again measured later arms against other sources than the arms before them - each blamed on the arm.
So they are read once, as the tree is, by size and hash, and each worker is given its own copy by
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
   BUILD-RED arm, has its paired control applied to the site as it was and built;
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

Whatever fails is kept to what it failed in, so one failure never costs the arms already judged. A
worker that cannot be made - its copy, its whole build - is retired alone, saying why, and the
workers beside it drive every arm; only where none could be made are their failures the leg's own.
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

Each arm's records are written as it ends, in `<run>/<leg>/arms/<arm>/`: `arm.json`, its line as
the ledger carries it, beside the logs of its build and its run, and of a paired control's build in
`control/`. Each control's are in `<run>/<leg>/controls/<runner>/`, and each worker's whole build in
`<run>/<leg>/workers/<n>/`. A leg's line counts its arms by verdict; below the table an ARMS block
names each selected arm that did not pass, and why; `--json` carries every arm beneath its leg - `arm`,
`verdict`, `failure`, `detail`, `durationSeconds`, `worker`, `cases`, `declaredCases`, `reds`,
`declaredReds`, `records` - a skipped one included.

A leg on a host is swept there, by the DssHarness there, on its own copy - with its own workers,
its own admission and the arms `--arms` named - and its arms travel back beneath its line, their
records staying on that host and its home written as `~`, as a leg's are (see "A host's home is
`~`"); the answer changed shape, which raised the host-agent protocol to 6.

### Self-test

`check-mutations --self-test` sweeps the fixture this tool carries, in place of the repository's
registry, which it does not need. The fixture is `tests/mutation-fixture/`, embedded whole into the
tool so every build carries the very fixture its own tests swept: a CMake library, a test binary
that writes its own JUnit report - nothing is fetched to build it - and a registry of seven arms,
one to each verdict an arm's design can reach on any machine: passed, as a TEST-RED arm, as a
BUILD-RED arm, and as an arm whose mutation is coupled across two files, neither edit building
without the other; violated; survived; unattributed; and failed. It is built as each selected leg builds - its
toolchain, configuration and sanitizer, its developer environment - and each arm is held to the
verdict it is designed to reach: one that reaches it passed, saying so; one that reaches another
verdict an arm's design decides is `violated`, naming both, which is this tool's defect with that
compiler, never the fixture's; and one that reaches no verdict of the judge's - stopped, not
admitted, poisoned - keeps it. The repository's end-to-end tests sweep the same fixture as a
repository's own project, and hold each arm to the same design.

The fixture is written where this tool keeps its own data (see *Heavy legs share a machine*), as
`<user data>/dssharness/mutation-fixture`, each file only where it differs and under a lock every
process on the machine takes: one fixture for every repository on the machine. Its workers are
kept apart from it, beside the tree of the leg self-tested, in a family of their own -
`<tree>.mutation-<key>s-<n>`, a few megabytes each. Kept beside the fixture they were
every repository's at once, one's toolchain refusing the build directory another's had made, with
nothing to remove them; beside the leg's tree they are that repository's, counted by its room,
removed by a `clean` of the leg, and with its worktree or its host's copy. A self-test asks no
host about the room a build of the leg's tree needs, and is placed by none; its workers are
measured where they are planned, and their paths reckoned by the fixture's own longest, in place of
the leg's reserve. It takes the leg's sweep lock, as a sweep of the leg does, and its binary runs
in its worker with what the leg's host gives it, nothing of the leg's tests. A leg on a host is
self-tested there, with the fixture that host's DssHarness carries.

## Reporting

Progress is one line per leg transition, not a stream of child process output.
Child output goes to a per-leg log file and is echoed only under `--verbose`. A leg another host
runs has its lines relayed from that host as they come; the host leaves the lines its dispatcher
says for itself - that the legs are starting, where each starts, and each one's verdict - since
said on both, each read twice. So the dispatcher keeps, rather than relays, the line a host's
command ends on where it ended badly, with what follows it: it says how the leg ended itself, by
the leg's line, or in the refusal it raises from what the host said, and a host's summary of its
one leg would otherwise read as the run's own. Such a line is kept with at most 200 lines after
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

Beneath it, `logs:` names where the run's records are, and each leg another host ran names
that host's own; `--json` carries the same as `runDirectory`, at the top and on such a leg (see
"Where a run's records live").

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
| 21 | Ran with nothing failing, but a leg reached no verdict, or a deletion, a fold, a hand-over or a move of an agent's base stopped part way; it is not a pass, and running it again, once what it names is dealt with, finishes it |
| 70 | The harness itself failed unexpectedly (a defect in the tool) |
| 130 | The run was interrupted before it finished; what it had already done is still reported |

`verify-git` keeps its own contract: `0` success, `1` git not installed,
`2` not a git repository. `legs` and `sync` exit `1` when a leg named with `--legs` cannot run,
or when no selected leg can, and `70` when whether a leg can run was never established, through
a defect in this tool. `build`, `test`, `run` and `clean` exit `1` too when no selected leg can
run and no failure turned one away, each leg's line saying why it cannot; `check-mutations`
exits `21` there (see below). `install-missing-tools` exits `1` when a tool is missing, out of
date or could not be installed, and `15` when a host could not be reached: a tool that is not
there and a host that did not answer call for different things. `check-anchor-balance` and
`check-anchor-citations` exit `1` on a finding, which is what they were asked to look for rather
than a failure of the command. `check-ci-legs` exits `1` when a leg is red and `2` when no job
is a leg - the matrix did not run, or `legJobPattern` matches none of its jobs - an empty answer
indistinguishable from every leg passing, and never read as one. `host-exec` returns the exit code of the command it ran on the host,
unchanged, or 15 when that command never reported how it finished.
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
