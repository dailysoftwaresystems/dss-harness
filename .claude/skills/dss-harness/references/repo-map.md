# Repository map, layering, build and toolchain

## Contents
- 1. Projects
- 2. Where things live
- 3. Layering
- 4. Build and toolchain
- 5. Running the suite
- 6. Canonical examples
- 7. Which document says what

## 1. Projects

[`DssHarness.slnx`](../../../../DssHarness.slnx) holds three projects, plus the workflows, the package-check action and
the version program as solution items.

| Project | What it is |
|---|---|
| [`src/DssHarness.Cli`](../../../../src/DssHarness.Cli/DssHarness.Cli.csproj) | The tool: assembly `dssharness`, packed as the .NET tool `DssHarness` with command `dssharness`. Argument parsing and dependency wiring only. Holds the one `<Version>` |
| [`src/DssHarness.Core`](../../../../src/DssHarness.Core/DssHarness.Core.csproj) | Every service, model and abstraction. Grants `InternalsVisibleTo` to the test project alone. Embeds the anchor-registry template and the mutation fixture |
| [`tests/dss-harness-test`](../../../../tests/dss-harness-test/dss-harness-test.csproj) | The xunit v3 suite, an executable on the Microsoft Testing Platform runner. Namespace `DssHarness.Tests`, one flat directory. Embeds three workflows so tests can pin them |

[`tests/mutation-fixture`](../../../../tests/mutation-fixture) is not a .NET project: it is a small CMake project with
one arm per verdict a mutation design can reach, embedded into Core and swept by `check-mutations --self-test`.
`MutationFixture.Designed` names each arm's designed verdict. Its texts are matched byte for byte, so never reformat
them.

## 2. Where things live

**Cli** (`src/DssHarness.Cli`):

| File | Holds |
|---|---|
| `Program.cs` | Registers every command and runs the parser |
| `CommandRunner.cs` | `Wire(...)`, `CommandContext` (`Get<T>()`, `Directory`, `Json`), and the one place an exception becomes an exit code (`Meaning`) |
| `GlobalOptions.cs` | `-v`, `-C/--directory`, `--no-prompt`, added to a command by `AddTo` |
| `CommandFlags.cs` | Reads flags from a parse that may have been refused, never throwing |
| `JsonAnswer.cs` | Each command's `--json` document for an exit its body never reached (refused, usage error, throw) |
| `HarnessServices.cs` | The dependency-injection composition root |
| `Commands/` | One static `XCommand.Create()` per command (several per file for the anchor, agent, orchestrator and worktree families). `HelpCommand.cs` holds every `help` topic's text |

**Core** (`src/DssHarness.Core`), one folder per concern:

| Folder | Concern (key types) |
|---|---|
| `Anchors` | Anchor registry rows, balance and citations (`IAnchorRegistryService`, `AnchorBalanceService`, `AnchorCitationService`) |
| `Build` | Build adapters and CMake/Ninja inspection (`BuildService`, `BuildDirectoryGuard`, `CMakeToolchainReader`) |
| `Ci` | `check-ci-legs` (`CiLegsService`) |
| `Commands` | Services for commands with no domain folder (`InitService`, `VerifyGitService`) |
| `Configuration` | `config.json`: model, load, validation, key lists (`HarnessConfig`, `HarnessConfigValidator`, `ConfigKeys`, `DefaultConfigFactory`) |
| `Execution` | A leg's run: phases, locks, ledgers, verdicts, admission, input watch (`LegExecutor`, `PhaseRunner`, `RunLock`, `LegLedger`, `LegVerdict`, `LegAdmission`) |
| `FileSystem` | `IFileSystem`/`PhysicalFileSystem`, `PathCase`, junctions, holds, verified copies |
| `Git` | `IGitClient`/`GitClient` (partial files for history and merges) |
| `Hosts` | Reaching and probing ssh and WSL hosts, the tool on each host (`HostCommandRunner`, `HostAgentService`, `ToolUpdateService`) |
| `Legs` | Leg placement and selection (`LegsService`, `LegPlacement`, `LegSelection`) |
| `LineEndings` | `.gitattributes` policy (`LineEndingService`) |
| `Mutations` | `check-mutations` (`MutationService`, `MutationRegistry*`, `ArmJudge`, `MutationFixture`) |
| `Orchestration` | Orchestrators and agents: seeding, folding, rebasing (`OrchestratorService`, `AgentService`, `AgentFold`) |
| `Output` | `IHarnessOutput`/`ConsoleHarnessOutput`, text and JSON reports |
| `Platform` | The operating-system seam (`IHostPlatform`, `IFilePermissions`, `IProcessTable`, `PathBudget`) |
| `Processes` | `IProcessRunner`/`ProcessRunner`, `ProcessRequest`/`ProcessResult`, `CommandLineText` (the one no-shell line splitter) |
| `Projects` | What `init` detects and seeds (`IProjectDetector`) |
| `Repository` | The harness context and layout, `GitIgnoreManager` |
| `Results` | `CommandOutcome`, `HarnessException`, `HarnessExit` (the shared exit codes) |
| `Runners` | Predefined runners and action files (`RunnerRunService`, `ActionFileParser`) |
| `Runs` | Leg runs, remote runs, `clean` (`LegRunService`, `RemoteLegRunner`, `CleanService`) |
| `SetUps` | `up`/`down` and a run's `--set-up`: lines, shared holds, bringing up around a leg (`SetUpService`, `SetUpHolds`, `LegSetUps`) |
| `Secrets` | Host connection data and its permission checks (`HostSecretsStore`, `DotEnvFile`) |
| `Sync` | Syncing a tree to hosts (`SyncService`, `SyncManifest`, `HostCopies`) |
| `Testing` | `test` and ctest (`TestService`, `TestInvocationResolver`) |
| `Tools` | `install-missing-tools` (`IToolProvisionService`) |
| `Worktrees` | Worktrees, root litter, host copies (`WorktreeService`, `RootLitterService`) |

## 3. Layering

From [`docs/architecture.md`](../../../../docs/architecture.md) "Layering", which is the authority:

- **Cli wires, Core works.** `Program.cs` parses arguments and wires dependencies; every rule lives in Core, which
  never references Cli. The assembly boundary enforces it.
- **The operating system is learned in one layer.** `HostPlatform`, `FilePermissionsFactory` and
  `ProcessTableFactory` decide what differs by OS. Everything above depends on `IHostPlatform`, `IFilePermissions`,
  `IProcessTable`, `IProcessRunner` and `IFileSystem`. The few primitives below that seam that ask the runtime directly
  are listed in the architecture document; do not add another without adding it there. Nothing above the seam
  branches on `OperatingSystem.Is*`.
- **Programs run through `IProcessRunner`**, the single seam to external tools, with arguments as a list, never a
  command-line string, so shell quoting is never part of the system. A bare program name is looked up on `PATH` alone.
- **Paths come from git.** Every repository path is resolved through git with links followed; a typed directory only
  tells git where to start.
- **`config.json` is strict.** An unknown key, a key written twice, a `null` the model forbids, or a reference to
  something undeclared is refused when the file is read, every problem at once.

## 4. Build and toolchain

- [`global.json`](../../../../global.json) pins the .NET 10 SDK (rolling forward) and selects the Microsoft Testing
  Platform test runner.
- [`Directory.Build.props`](../../../../Directory.Build.props) applies to every project: `net10.0`, nullable on,
  implicit usings, `TreatWarningsAsErrors`, `GenerateDocumentationFile` (so a stale `<param>` fails the build; only
  CS1591 is suppressed), the NuGet audit on every advisory, and **`InvariantGlobalization`** — culture-aware APIs
  answer invariantly, and `string.Normalize` changes nothing, so never rely on Unicode normalization.
- **Everything a build writes lands under `build/`** (`UseArtifactsOutput`): `build/bin/<Project>/<debug|release>/`,
  `build/obj/...`, packages in `build/package`. Deleting `build/` is a complete clean.
- **Dependencies:** YamlDotNet and System.CommandLine (Core), Microsoft.Extensions.DependencyInjection (Cli), xunit v3
  and NSubstitute (tests). No new package without the maintainer's approval.
- Every file is stored and checked out with LF line endings ([`.gitattributes`](../../../../.gitattributes)); there
  is no CRLF exception.

## 5. Running the suite

```bash
dotnet build DssHarness.slnx
./build/bin/dss-harness-test/debug/dss-harness-test --filter-class "DssHarness.Tests.PackagePipelineTests"
./build/bin/dss-harness-test/debug/dss-harness-test --filter-method "DssHarness.Tests.HelpTests.*"
./build/bin/dss-harness-test/debug/dss-harness-test
```

- Run the **built binary** (`.exe` on Windows), as CI does. CI adds `--xunit-diagnostics on --long-running 120
  --show-slowest-tests 20`, which report each test still running after two minutes and the slowest; add them to a
  whole-suite run. Both filter flags repeat and OR among
  themselves, but `--filter-class` together with `--filter-method` runs nothing: filter one class's methods with
  `--filter-method "<Class>.*"`. xunit's own `-class`/`-method` switches are refused.
- **A run that reports zero tests is a runner problem, never a pass.**
- **Some classes are slow** — they create real repositories, worktrees, host copies, orchestrators, runner runs, or
  start the CLI (`Worktree*`, `HostCopiesTests`, `OrchestratorServiceTests`, `RunnerRunServiceTests`,
  `CliEndToEndTests`, `CheckMutationsEndToEndTests`, `GitClientTests`). While iterating, pin them to the methods you
  touched; the whole suite still runs before a push.
- Tests that build real CMake projects need `cmake`, `ninja` and a C and C++ compiler on `PATH` (and Visual Studio's C++
  build tools on Windows). Missing, they skip naming what is missing; with `DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS=1`, as
  CI sets it, they fail instead ([`BuildTools.cs`](../../../../tests/dss-harness-test/BuildTools.cs)).
- **Linux from a Windows machine:** copy the tracked and untracked-unignored files
  (`git ls-files -co --exclude-standard`) into a WSL distribution, build Release there and run
  `DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS=1 ./build/bin/dss-harness-test/release/dss-harness-test`. If your NuGet
  configuration names other feeds, restore with a `nuget.config` that clears them and names nuget.org alone. Run it
  after the Windows suite, not beside it: timing tests fail when the two share the CPU.

## 6. Canonical examples

| Want | Look at |
|---|---|
| A whole command, end to end | [`CheckRootLitterCommand.cs`](../../../../src/DssHarness.Cli/Commands/CheckRootLitterCommand.cs) — `Name`, `Create()`, `GlobalOptions.AddTo`, `CommandRunner.Wire`, one service call, a `CommandOutcome` |
| An enum status mapped to exit codes | [`VerifyGitCommand.cs`](../../../../src/DssHarness.Cli/Commands/VerifyGitCommand.cs) |
| A Core service with its interface | [`RootLitterService.cs`](../../../../src/DssHarness.Core/Worktrees/RootLitterService.cs) — documented exceptions, a result record, collaborators through `IHarnessContextLoader`, `IGitClient`, `IFileSystem` |
| A test class on real collaborators | [`RootLitterServiceTests.cs`](../../../../tests/dss-harness-test/RootLitterServiceTests.cs) |
| Help held to the code it describes | [`HelpTests.cs`](../../../../tests/dss-harness-test/HelpTests.cs) |
| The exit-code contract | [`HarnessExit.cs`](../../../../src/DssHarness.Core/Results/HarnessExit.cs) and [`ExitCodeContractTests.cs`](../../../../tests/dss-harness-test/ExitCodeContractTests.cs) |
| `--json` on every exit | [`JsonAnswer.cs`](../../../../src/DssHarness.Cli/JsonAnswer.cs) and [`StoppedDocumentTests.cs`](../../../../tests/dss-harness-test/StoppedDocumentTests.cs) |

## 7. Which document says what

| Document | Says |
|---|---|
| [`README.md`](../../../../README.md) | What a user meets: commands, the layout `init` gives a consuming repository, legs and hosts, anchors, building from source |
| [`docs/architecture.md`](../../../../docs/architecture.md) | Why and how, section by section: layering, config, worktrees, orchestrators and agents, anchors, hosts and legs, parallel runs, leg integrity, contamination, disk space, sync, runners, mutation testing, reporting, exit codes, timeouts |
| [`docs/releasing.md`](../../../../docs/releasing.md) | Channels, Deploy, Package Pipeline, the required check, security posture |
| `dssharness help [topic]` | The user's reference, generated where the code owns the facts (exit codes, config keys, defaults); `dssharness help` lists the topics |
