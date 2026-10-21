# Release and CI

[`docs/releasing.md`](../../../../docs/releasing.md) is the authority for everything here. When this file and it
disagree, it wins and this file is fixed.

## Contents
- 1. Who does what
- 2. The workflows
- 3. The test matrix
- 4. The required check
- 5. Versions and channels
- 6. Security posture
- 7. Changing a workflow

## 1. Who does what

- **A change stops at "ready to merge".** It is a pull request from a branch in this repository whose required check
  is green. A maintainer merges it and runs **Deploy** from the Actions tab. Never dispatch Deploy or Package Pipeline
  yourself, never push to `main` or to a `release/*` branch, and never force-push, amend a pushed commit or rebase a
  branch that is up for review.
- **One pull request at a time.** While one is open and not yet released, new work goes into it, not into a second
  or a stacked one.
- **Never bump `<Version>` in a pull request.** Deploy's beta run bumps it and commits the bump as `v<version>`.
- **A stable release reaches people at once.** Consumers that install the tool unpinned get every stable release on
  their next CI run, so the beta channel is where a release is tried first.

## 2. The workflows

| Workflow | Fires on | Does |
|---|---|---|
| [`pipeline-pr.yml`](../../../../.github/workflows/pipeline-pr.yml) ("Pipeline") | pull request to `main`, push to `main` | Runs `test.yml`, packs, installs and runs the packed tool, posts the required status |
| [`test.yml`](../../../../.github/workflows/test.yml) ("Test") | called by `pipeline-pr.yml` | Builds and runs the suite on every runner of the matrix |
| [`deploy.yml`](../../../../.github/workflows/deploy.yml) ("Deploy") | by hand: `channel` beta or stable, optional `version` for beta | `plan` pins the commit, `promote` checks, bumps (beta), pushes atomically and starts Package Pipeline naming the commit. Runs no test suite |
| [`pipeline-pkg.yml`](../../../../.github/workflows/pipeline-pkg.yml) ("Package Pipeline") | started by Deploy, or by hand for a release branch | `package` builds, packs and verifies read-only; `tag`; `nuget` for stable; `release` last. Runs no test suite |
| [`cleanup-cache.yml`](../../../../.github/workflows/cleanup-cache.yml) | pull request closed | Deletes that pull request's caches |

Both pipelines verify a package through one composite action,
[`.github/actions/verify-tool-package`](../../../../.github/actions/verify-tool-package/action.yml): it installs the
tool from the local package alone and checks the version it reports. Versions are read and bumped by one file-based
program, [`.github/scripts/version.cs`](../../../../.github/scripts/version.cs) (`read`, `bump`, `suffix`).

## 3. The test matrix

`test.yml` runs on Linux, Windows and macOS, each on x86_64 and arm64, all on native runners: .NET under full
emulation is unsupported, so no leg is emulated. Each leg restores, builds the solution in Release (warnings are
errors and the NuGet audit is on, from [`Directory.Build.props`](../../../../Directory.Build.props)), installs `ninja`
if the runner lacks it, and runs the test binary directly, not through `dotnet test`, with
`DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS=1` so a test that needs a real build fails where a tool is missing instead of
skipping green.

A change that passes on one operating system can fail on another: the file system folds case on Windows and macOS
and not on Linux, macOS keeps a file's old spelling on a rename where Windows takes the new one, and test doubles that
stand for a folding file system must answer alike everywhere. Run the whole suite on Windows and on Linux before
pushing (see [testing-and-conventions.md](testing-and-conventions.md) §1).

## 4. The required check

Merging into `main` waits for one commit status, `Pipeline / ci-check result`, which the organisation's ruleset
requires. The `ci-check` job in `pipeline-pr.yml` posts it once the matrix and the package check finish: `success`
only when both succeeded, and the job fails whenever the status is a failure. A pull request from a fork cannot post
it, so a maintainer runs such a change from a branch here. Read required checks from rulesets
(`gh api repos/<owner>/<repo>/rules/branches/main`): `branches/main/protection` answers that `main` is unprotected.

Every release stands on this check: neither Deploy nor Package Pipeline runs a suite. Deploy promotes `main`'s
commit as the check passed it (for beta, with only `<Version>` bumped on top) and stable promotes what beta
released. Never add a suite back to either; a slow runner is met by the Test step's `timeout-minutes` in
`test.yml` (120, the job's 135).

## 5. Versions and channels

- **One version source:** `<Version>` in
  [`src/DssHarness.Cli/DssHarness.Cli.csproj`](../../../../src/DssHarness.Cli/DssHarness.Cli.csproj). The package id is
  `DssHarness` and the command is `dssharness`. Packages are written to `build/package`.
- **beta** is cut from `main`. It bumps the patch unless a version is given (a minor or major change is always
  explicit), commits the bump, moves `main` and `release/beta` in one atomic push, and publishes a GitHub prerelease
  `v<version>-beta` with the package attached. Nothing goes to nuget.org.
- **stable** promotes exactly the commit beta released under the same version to `release/stable`, publishes to
  nuget.org by trusted publishing, then releases `v<version>` on GitHub.
- **Package Pipeline resumes:** started again for the same commit, it reuses a tag that already names the commit,
  neither rebuilds nor pushes a version nuget.org already has, and creates the GitHub release last. The release is
  what marks a version published.
- **nuget.org shows a new version only minutes after accepting it.** Until then the version is missing from public
  queries, and a run started again inside that window stops and says so. That is normal: confirm the push said it was
  created, then wait.
- **Refusals** — nothing new to publish, a publish not finished, a diverged release branch, a version already
  published or moving backwards, a beta never released, and more — are listed in `docs/releasing.md` "Refusals worth
  knowing". "Could not tell" refuses, as "does not exist" never stands in for it.

## 6. Security posture

- The repository is public. Nothing committed — code, docs, tests, commit messages, branch names, pull request text —
  holds a secret, a private repository's name, or the name of an internal tool or service.
- No long-lived publishing key exists anywhere: nuget.org trusts the `nuget` job's short-lived OIDC token, and that job
  runs in the `nuget` environment, which admits `release/stable` alone.
- `pipeline-pr.yml` and the `test.yml` it calls hold `contents: read` and check out without leaving a credential in
  the working copy. The exceptions are `ci-check` (`statuses: write`, checks out nothing) and `cleanup-cache.yml`
  (`actions: write`, skipped for forks). `pull_request_target` and `secrets: inherit` are used nowhere.
- Every action is pinned to a full commit SHA with its version in a comment. Dependabot
  ([`.github/dependabot.yml`](../../../../.github/dependabot.yml)) proposes action updates weekly, the SHA and the
  version together.

## 7. Changing a workflow

1. The tests embed `test.yml`, `pipeline-pkg.yml` and `deploy.yml` (`EmbeddedResource` in
   [`tests/dss-harness-test/dss-harness-test.csproj`](../../../../tests/dss-harness-test/dss-harness-test.csproj)) and
   hold steps and jobs to what they must say through
   [`WorkflowFiles`](../../../../tests/dss-harness-test/WorkflowFiles.cs) (`Lines`, `Job`, `StepNamed`,
   `StepRunning`). Find the tests a step is pinned by with `git grep -n "WorkflowFiles\." tests`, and change them in the
   same commit: `PackagePipelineTests` pins that neither Deploy nor the package pipeline runs a suite and that
   what Deploy starts publishes the commit it pinned and promoted; `BuildToolsTests` pins the build-tools variable on the step that runs the suite.
2. A workflow that newly runs the suite sets `DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS: "1"` on that step, last among its
   variables, and joins the `BuildToolsTests` theory.
3. Pin any new action to a full commit SHA with `# v<version>` beside it.
4. Move the workflow's header comment, the "Pipelines" table and any prose in `docs/releasing.md` with the change.
5. A workflow edit is proven only by CI on the pull request; nothing here runs a workflow locally.
