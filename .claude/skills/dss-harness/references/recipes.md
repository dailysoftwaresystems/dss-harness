# Change recipes

Each recipe lists what to touch, in order, and the tests that already hold the piece. Line numbers are left out on
purpose: find a member by name. Sentences of `README.md` and `docs/architecture.md` that tests read through
`Prose.Document` may be re-wrapped freely but not reworded without the test.

## Contents
- 1. A command
- 2. A `config.json` key
- 3. An exit code
- 4. A verdict
- 5. A help topic
- 6. What `init` writes into a repository
- 7. Records a command writes
- 8. A workflow
- 9. A language of the codebase graph
- 10. Before calling any change done

## 1. A command

1. **The service, in Core** — `src/DssHarness.Core/<Area>/XService.cs` with `public const string CommandName`. It
   returns a `CommandOutcome` (`Ok`, `Usage`, `Refused`, `Failed`; `Data` carries the standard-output document) or
   throws `HarnessException(HarnessExit.*, message)`. Register it in `ServiceRegistration.AddDssHarness`
   (`src/DssHarness.Core/Composition/ServiceRegistration.cs`).
2. **The command, in Cli** — `src/DssHarness.Cli/Commands/XCommand.cs`: `internal const string Name =
   XService.CommandName`, `internal static Command Create()`, its options, `GlobalOptions.AddTo(command)` (and the
   `DispatchOptions` a leg-running command takes), then `CommandRunner.Wire(command, body, jsonAnswer)`. A command that
   keeps a run uses the `Wire` overload with a `RunId`, which prints `run <id>` before anything can refuse. Read flags
   that may have been refused through `CommandFlags`; read `--json` only through `CommandContext.Json`.
3. **Register it** in `Program.cs`; the order there is the order of the root `--help`. A command meant for the tool
   itself (on a host) is `Hidden`.
4. **A command that must finish what it began** once it writes joins `PointOfNoReturn.Commands` and
   `PointOfNoReturnTests`.
5. **`--json`** — declare its `JsonAnswer` at `Wire` time (`JsonAnswer.Ledger(...)`, or one built on
   `ReportJson.Stopped`), so every exit, a parser refusal included, answers in the command's own document. Add it to
   `StoppedDocumentTests.OwnDocuments`, and to the architecture sentence listing the commands that answer `--json`,
   which that class pins.
6. **Help** — the root `--help` lists it from its description (over ten characters). Explain it in the topic it belongs
   to (`HelpTopics.Render*`, in `src/DssHarness.Core/Help/HelpTopics.cs`); the overview's "Getting started" list is
   curated, not complete.
7. **Docs** — the `README.md` "Commands" table lists every command a user types (not the hidden ones);
   `docs/architecture.md` "Status" names every implemented command. No test checks either list: update both.
8. **Tests** — the service on real collaborators, the command through `CliRunner`. `HelpTests` and
   `StoppedDocumentTests` already walk every listed command for working help and a refused command line.

## 2. A `config.json` key

`JsonConfigStore.Load` refuses unknown, duplicate and forbidden-`null` keys (`JsonConfigOptions`), then
`HarnessConfigValidator` reports every problem at once.

1. Add the property to its model (`HarnessConfig`, `RunnerModels`, `MutationSettings`, `AdmissionSettings`,
   `HostModels`...), `required` where it must be given. A value a string or a per-platform map may give derives from
   `PerPlatformText` with a `PerPlatformTextConverter` (`BuildOutput`, `SetUpLine`); a command line is split by
   `CommandLineText` and its program held to `ActionToolPolicy`, never a shell.
2. Give it a `[Description("...")]`: `ConfigKeys.Of<T>()` turns descriptions into help.
3. Add its rules to the matching `HarnessConfigValidator.Validate*`.
4. Seed it in `DefaultConfigFactory.Create` only if `init` should write it.
5. Retiring a key: a message in `MisplacedKeys.Retired` saying what took its place; never silently ignore it.
6. Action-file keys live in `ActionFileKeys`; `ActionFileParser` accepts exactly the names listed there.
7. Help is generated for runners, action files, mutations and admission; `RenderConfig` is written by hand. The tests
   that list keys exactly (`ConfigKeysTests`, `HelpTests` for runners, mutations and admission,
   `ActionFileParserTests.EveryListedKey_IsRead`) change with the key.
8. Docs: `docs/architecture.md` "Reading config.json", "Predefined runners", "Action files"; README "Legs and hosts"
   when users meet the key.

## 3. An exit code

- **Shared codes** (10 and up): a `public const int` on `HarnessExit` plus its explanation in `BuildDescriptions`;
  `help exit-codes` picks it up by reflection. `ExitCodeContractTests` holds range, uniqueness and explanations.
- **Per-command codes** (1-9) live beside their command (`LegExit`, `LegsExit`, `AnchorExit`, `ToolsExit`, `CiExit`,
  `VerifyGitStatus`); add a new one to `PerCommandCodes_StayInsideTheirRange` and the names
  `ExitCodeTopic_DocumentsEveryPerCommandCode` lists. A command may also name a shared code for its own case —
  `MutationService.NothingRuns` is `HarnessExit.Incomplete` (21) — and such a name joins neither list.
- **By hand:** `HelpTopics.RenderExitCodes`, the `docs/architecture.md` "Exit codes" tables, the README "Commands"
  paragraph on exit codes. No test reads the architecture tables.

## 4. A verdict

The vocabulary is closed: a new verdict is a design decision.

1. A member of `LegVerdict` and a row of `Verdicts.Table` (display name, failure or not, rank — the table stays
   ordered by precedence — exit code, how a sweep reads it); decide `Verdicts.IsSkip`; map a refusal in
   `Verdicts.ForRefusal` if it has its own code.
2. A remedy in `HelpTopics.Remedies`; `RenderVerdicts` lists the table itself.
3. `docs/architecture.md` "Verdict vocabulary (closed)" table and its precedence sentence; `RenderExitCodes`.
4. `LegVerdictTests`, `ExitCodeContractTests` and the verdict-topic tests in `HelpTests`.

## 5. A help topic

Add `new("name", ["alias"], "summary", RenderX)` to `HelpTopics.Topics` (`src/DssHarness.Core/Help/HelpTopics.cs`) and
write `RenderX`. That one list drives
the overview, the argument's description and the unknown-topic message. Names are `[a-z-]+`. `HelpTests` renders
every advertised topic and checks every command a topic tells the user to type is spelt as installed (`dssharness`,
lowercase); add sentence assertions of your own with `Prose.Words`. Update the README `help [topic]` row.

## 6. What `init` writes into a repository

- Layout constants: `HarnessLayout` (`PlaceholderDirectories`, `RootSlots`). A new slot at a repository's root joins
  `RootSlots` (`init`'s ignore rules and placeholders, `SlotIgnored`) and, when it belongs to the machine that made
  it, `SyncConfig.NeverTransferFloor` too (what `sync` withholds and never deletes on a host). No test ties the two
  lists together: check both.
- Creation: `InitService` (`BuildIgnoreRules`, `KeptInGit`, `EnsurePlaceholderDirectory`, `EnsureConfiguration`);
  the seeded configuration is `DefaultConfigFactory`.
- The managed `.gitignore` block: `GitIgnoreManager`. Retired markers are read forever and never written; a block
  under them is rewritten in place under the current ones. Never drop the retired handling.
- Tests: `InitServiceTests`, `GitIgnoreManagerTests`, `GitIgnoreRecoveryTests`, `ManagedIgnoreCheckTests`,
  `DefaultConfigFactoryTests`, the `Init_*` tests of `CliEndToEndTests`, and the layout topic in `HelpTests`.
- Docs: `HelpTopics.RenderLayout`, README "Layout", `docs/architecture.md` "Worktrees".
- A change here reaches every consumer on their next `init`: say what moves in the pull request's description.

## 7. Records a command writes

A run's records live in `.harness-config/runs/<run id>/` of the tree that ran it (`HarnessLayout`); other records
have their own fixed place (`mutation-record`, `host-copies`, `.orchestrators`, `.legs`, `.mutations`). A command that
writes records or logs **names where they landed on every exit**, early and refused ones included: `run <id>` first
(`RunLine`), `logs: <dir>` and each leg's records in text, `runId`, `runDirectory`, `records` and `logs` in `--json`,
paths shown through `IHarnessOutput.Shown` so a host writes its own home as `~`. Add a test like
`LegRunServiceTests.ARun_NamesWhereItsRecordsAre_InTheTreeThatRanIt`. Docs: `docs/architecture.md` "Where a run's
records live" and "Reporting".

## 8. A workflow

See [release-and-ci.md](release-and-ci.md) §7. Only `test.yml`, `pipeline-pkg.yml` and `deploy.yml` are embedded
and tested; embedding another takes one `EmbeddedResource` line in the test project.

## 9. A language of the codebase graph

1. **One class** in `src/DssHarness.Core/Codebase/Languages/` implementing `ICodeGraphExtractor`, made with no
   argument: its `Language` (lower-case letters and digits), `Version` 1, its `Claims` (exact names, extensions with
   their dot, a content test on each claim another implementation shares), `Outlines`, `Help` and a pure `Extract`
   that answers any text, a cut of one included, and never throws. Nothing registers it: the registry's one scan finds
   it, and nothing outside `Languages/` may name it (`CodeGraphExtractorsTests`).
2. **Its tests** — `tests/dss-harness-test/<Name>ExtractorTests.cs` deriving from
   `CodeGraphExtractorContractTests<T>`, with fixtures covering each claim, the nodes each must hold (with lines) and
   the edges linking them must give. Run them once with `DSSHARNESS_STORE_EXTRACTS=1` to write the stored extracts
   under `tests/dss-harness-test/fixtures/codebase-graph/<language>/v1/`, read them, and commit them.
3. **A change of what it answers** for any file raises its `Version` and stores the extracts again under the new
   version; the stored ones of the old version stay as they are. A change of what the linker answers raises
   `CodeGraphLinker.Revision` instead.
4. Help, `codebaseGraph.languages`, V52 and the step read the registry: nothing else changes. Docs: what the
   language reads and does not see is its `Help`, which `help codebase-graph` prints; `docs/architecture.md` "The
   codebase graph" names no language but the fallback.

## 10. Before calling any change done

1. The whole suite passes on Windows and on Linux ([testing-and-conventions.md](testing-and-conventions.md) §1).
2. Each new test goes red with the change reverted.
3. Help, README, `docs/architecture.md`, `docs/releasing.md` and every sibling sentence say the new behaviour, in the
   same commit; search for the old wording.
4. The rule lives in one place; nothing was copied.
5. Every finding of a review is fixed at its cause, not narrowed or deferred silently.
6. Nothing committed names a secret, a private repository or an internal tool.
7. `git status -sb` shows the branch you meant before each commit and push; never `main`.
