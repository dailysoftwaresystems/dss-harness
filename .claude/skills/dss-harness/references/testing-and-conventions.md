# Testing discipline and coding conventions

## Contents
- 1. Before a push: the whole suite, on Windows and on Linux
- 2. Strict tests — the rules
- 3. Test infrastructure
- 4. Platform limits and build tools
- 5. Documents and help are held to the code
- 6. Coding conventions
- 7. Don't

## 1. Before a push: the whole suite, on Windows and on Linux

CI runs every test on Linux, Windows and macOS, each on x86_64 and arm64. A change that passes the tests that look
related, or the whole suite on one system, still breaks CI when it touches something tests fake: a file system that
folds case, a git client, a platform answer. So before every push:

1. Build and run the **whole** suite on the system you work on — see [repo-map.md](repo-map.md) §5.
2. Run the **whole** suite on Linux too (WSL from Windows), with `DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS=1`, after the
   first run finishes, not beside it.
3. When a change touches a helper that tests fake, find every double of it (`git grep -n "override" tests`, and the
   doubles named in §3) and make each answer the new question on every platform.

## 2. Strict tests — the rules

**A test that cannot go red protects nothing.** Every test asserts the strongest property the setup makes provable.

1. **Exact values.** `Assert.Equal` on a count, a message, an exit code or a list when it is known exactly; never
   `Assert.True(x > 0)` or a size alone where the whole sequence is known. `Assert.Single` where one is expected.
2. **Exit codes by name.** `Assert.Equal(HarnessExit.Refused, ...)`, `LegExit.*` and the other per-command constants,
   never a bare number.
3. **Whole messages.** Pin a refusal or a dry-run line whole. Where text wraps, compare sentences through
   `Prose.Words`, which reads any run of whitespace as one space.
4. **Ordinal strings.** Every string comparison spells `StringComparison.Ordinal`.
5. **Cancellation.** Pass `TestContext.Current.CancellationToken` to every call that takes a token.
6. **Red when reverted.** Before calling a test done, revert the change (or break the line it guards) and watch it
   fail. A test that passes both ways is deleted or fixed, never kept.
7. **What is not there.** Assert that nothing else happened: no extra line, no file written, no second entry, the
   tree left as it was.
8. **One test per behaviour, named as a sentence.** `Subject_Behaviour[_Qualifier]`, articles kept:
   `ANewFileBesideATrackedOneInAnotherCase_IsLitter`. A `/// <summary>` states the rule the test holds and why it
   matters, in plain sentences. A `//` comment inside a test says why an assertion exists, never what the next line
   does.
9. **Real collaborators first.** Services that do real file and git work are tested on real temporary repositories
   (`HarnessFactory`, `TempDirectory`); a substitute stands in only where the service merely consults something, and a
   one-fault double only where the real thing cannot be made to fail on demand.

## 3. Test infrastructure

All in [`tests/dss-harness-test`](../../../../tests/dss-harness-test):

| Helper | Use it for |
|---|---|
| `HarnessFactory` | Real services (git, `PhysicalFileSystem`, `ProcessRunner`, `HostPlatform`, output to a `StringWriter`) over an initialised repository; `WriteConfig`, `CommitAllAsync` |
| `TempDirectory` | A disposable real directory under the test root; `Combine`, `WriteFile`, `WriteProgram` |
| `TestHost` | Module initialiser: isolates git configuration and the temporary root, and lets the test binary act as a child process |
| `CliRunner` | Runs the real built CLI for end-to-end and exit-code tests, and parses help output |
| `PassThroughFileSystem` | Base for one-fault `IFileSystem` doubles (`CaseOf` for a file system that folds case, `UndeletableFile`, `UnlistableDirectory`, full-disk and unlistable variants) |
| `HostTestDoubles` | Scripted hosts and copies (`ScriptedHostCommands`, `NoHosts`, `NoHostCopies`, `RecordingLauncher`) |
| `QuietProcessRunner`, `QuietProcessTable` | A program that says one line, stays silent for a given life and exits 0 (for stall bounds); a machine running only this test process and any others given |
| `OrchestrationKit`, `SyncKit`, `AdmissionKit`, `ActionKit` | Ready-made scenes for those areas |
| `ManualClock`, `JumpingClock`, `SteppingClock`, `ClockWatch` | `TimeProvider` doubles; `ClockWatch` tells a test the wall clock stepped, and real-build tests skip on it |
| `PathAssert`, `Prose`, `TestLinks`, `HeapWatch` | Path equality across platforms, prose matching and the repository's documents, links where the system allows them, memory measured |
| `WorkflowFiles` | The embedded workflows, read as lines, jobs and steps — see [release-and-ci.md](release-and-ci.md) §7 |
| `BuildTools` | Skip or fail a test that needs real build tools |

Process-runner doubles are mostly private nested classes of the test class that needs them; git-client doubles wrap
the real client and intercept one call.

## 4. Platform limits and build tools

- A test limited to a platform says so on its first line with xunit v3's `Assert.SkipUnless` / `Assert.SkipWhen` /
  `Assert.Skip` and a one-sentence reason: `Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify is Linux's.")`.
  Where the same behaviour differs by platform, each side has its own test and each names the other.
- Prefer asking the file system over asking the OS: a test about case folding asks `IgnoresCaseAsync` or the disk,
  since macOS and Windows fold case and Linux does not.
- `BuildTools.Need(...)` skips a test whose tools are missing, naming them, and fails it instead when
  `DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS` is set. Visual Studio on a machine that cannot hold it always skips.
- Tests that measure memory or count system instances sit in non-parallel collections.

## 5. Documents and help are held to the code

- **Help is documentation and is tested like it.** Every listed command answers `--help` with a real description;
  topics are pinned by sentence; where the code owns the facts — exit codes, config keys, defaults, verdicts — the
  help is generated from the code and a test walks the code to prove every fact appears.
- **The README and the architecture document are read by tests** (`Prose.Document("README.md")`,
  `Prose.Document("docs", "architecture.md")`) where they promise a behaviour; a test interpolates `nameof(...)` so a
  rename breaks it.
- **When behaviour changes, its help text, README section and architecture paragraph change in the same commit**, and
  every sibling sentence that says the same thing elsewhere changes with them. Search for the old wording before
  calling it done.
- **The exit-code tables in `docs/architecture.md` are written by hand**; `HarnessExit` says so. A new code updates
  them in the same commit.

## 6. Coding conventions

- **One owner per rule.** Before adding code, find the type that already decides the question and extend it. Never
  copy a rule into a second place, and never keep two spellings of one fact.
- **Abstractions, not the runtime.** Above the platform seam, new code goes through `IFileSystem`,
  `IProcessRunner`, `IGitClient`, `IHostPlatform`, `IHarnessOutput` and `TimeProvider` — see
  [repo-map.md](repo-map.md) §3. A few older Core files still call `File.*` directly; do not add more. Core never
  touches the console: `ConsoleHarnessOutput` is its one writer. The Cli writes it directly only for `help`, a
  parser-refused command line's errors, the superuser prompt and the host commands that hand the tool its streams.
- **Shape:** file-scoped namespaces; classes `sealed` unless built to be derived; records for data; primary
  constructors with `_camelCase` fields; collection expressions; `InternalsVisibleTo` the tests is the one seam.
- **Async:** async work that waits on I/O takes a `CancellationToken cancellationToken` and passes it on. The
  long-standing seams (`IGitClient`, `IFileSystem`, `IProcessRunner`, `IProcessTable`, `ISyncTransport`) and the
  services' entry points declare it `= default`; newer internal seams take it without one. Core awaits with
  `.ConfigureAwait(false)`.
- **Names are words.** No abbreviations (`cancellationToken`, `entry`, `index`); `ex` for a caught exception.
- **Documentation comments** on public and internal types and members, in plain full sentences that state the
  contract and the non-obvious reason. No comment repeats what well-named code says, names a task or a pull request,
  or narrates. No `TODO`/`FIXME`/`HACK`; no warning suppressed without a reason beside it.
- **Failing:** a service returns a `CommandOutcome` (`Ok`, `Usage`, `Refused`, `Failed`) or throws
  `HarnessException` with a `HarnessExit` code; it never writes to the console or calls `Environment.Exit`.
  `CommandRunner.Meaning` alone turns exceptions into codes. Zero always means success, and "what you asked about
  failed" never shares a code with "the harness could not run".
- **Refusals** are full sentences: the names and paths in single quotes, what was and was not changed ("Nothing was
  removed."), and the remedy after a semicolon. "Could not tell" refuses; it never reads as "does not exist".
- **`--json` answers on every exit in the command's own document shape**, refused and stopped runs included
  (`JsonAnswer`). A command that writes records or logs names where they landed, in text and in `--json`, on every
  exit, early ones included.
- **Retired markers are read forever and never written; retired keys are refused.** A renamed `.gitignore` marker
  is still recognised where users have it on disk, and only the new one is ever written (`GitIgnoreManager`). A
  retired `config.json` key is refused like any unknown key, with a message naming what took its place
  (`MisplacedKeys`): a key that loads is a key that is read.

## 7. Don't

- Don't assert a size where the sequence is known, a substring where the whole message is known, or a number where an
  exit-code constant exists.
- Don't keep a test that stays green with the change reverted.
- Don't add `OperatingSystem.Is*`, `File.*`, `Directory.*` or `Process.Start` above the platform seam, or a `Console`
  call anywhere in Core.
- Don't push after running only the tests that look related, or only on one system.
- Don't treat a run that reports zero tests as a pass.
- Don't change a help sentence, a doc paragraph or a refusal without searching for every other place that says it.
