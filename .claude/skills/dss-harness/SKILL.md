---
name: dss-harness
description: >
  Repository guide for DssHarness, the cross-platform .NET tool (command `dssharness`) that builds and tests a
  repository's legs locally, over WSL and over ssh, keeps worktrees, orchestrators and agents, anchor registries and
  mutation checks. Use this whenever working in this repo and you need to know where something lives or what the
  local rules are — the Cli/Core layering and the platform seam, IFileSystem/IProcessRunner/IGitClient, config.json
  strictness, exit codes and verdicts, --json answers, help text held to the code, the strict xunit v3 test posture,
  running the suite through the test binary on Windows and Linux, the workflows, Deploy and Package Pipeline. Also
  use it before adding a command, a config key, an exit code, a verdict, a help topic, or changing what `init` writes
  or a workflow, even if the user never says "skill". This skill explains how the repo works; it does not drive a
  release. When it disagrees with docs/, README.md or the help, those win.
user-invocable: true
argument-hint: "[topic or question]"
---

# DssHarness — Repository Guide

One .NET tool, run the same way on Windows, macOS and Linux, that builds and tests a repository's legs — here, over
WSL and over ssh — and refuses, with the reason, whatever it cannot prove.

## When to use

- You need to know how an area works before changing it, or where the canonical example lives.
- You are about to add a command, a `config.json` key, an exit code, a verdict or a help topic, or change what `init`
  writes or a workflow.
- You need the local conventions: they are mandatory and stricter than general C# habit.

**Not this skill:** publishing a release. A maintainer merges and runs Deploy; a change stops at "ready to merge".

**Authority order:** [`docs/architecture.md`](../../../docs/architecture.md),
[`docs/releasing.md`](../../../docs/releasing.md), [`README.md`](../../../README.md) and `dssharness help` all outrank
this skill, and the tests that hold them outrank all four. When they disagree, they win and this skill is fixed.

## The rules that actually break things

1. **Core works through its seams.** Cli parses and wires; every rule lives in Core. Nothing above the platform
   layer branches on the operating system, and new code there reaches files, programs, git and the console through
   `IFileSystem`, `IProcessRunner`, `IGitClient`, `IHostPlatform` and `IHarnessOutput` rather than `File.*`,
   `Directory.*` or `Process.Start`. One behaviour everywhere is what the tool promises.
2. **A test that cannot go red protects nothing — and green on one system proves nothing.** Exact assertions, a test
   that fails with its change reverted, and the whole suite run on Windows and on Linux before every push: CI runs every
   runner of `test.yml`'s matrix, and a double that answers one platform's question wrongly fails the others.
3. **One owner per rule, and the words move with it.** Extend the type that already decides a question; never copy a
   rule. Help, README and architecture say what the code does, and when behaviour changes every sentence that states
   it changes in the same commit.

Everything else — strict `config.json`, exit codes that never mix "it failed" with "the harness could not run",
`--json` in the command's own shape on every exit, records named on every exit, refusals that say what was left
untouched, retired `.gitignore` markers read forever and never written, retired config keys refused naming what
replaced them, comment and naming style — is in the references and is
equally mandatory.

## Standing rules — one line each

- **Run the suite through the built binary**, never trusting a run that reports zero tests; never mix
  `--filter-class` with `--filter-method` → [references/repo-map.md](references/repo-map.md) §5.
- **The whole suite on Windows and on Linux before each push** → [references/testing-and-conventions.md](references/testing-and-conventions.md) §1.
- **Help, README and architecture are held to the code by tests** → same file, §5.
- **One pull request at a time; never push to `main`, force-push, dispatch Deploy or Package Pipeline, or bump
  `<Version>`** → [references/release-and-ci.md](references/release-and-ci.md) §1.
- **Deploy and the package pipeline run no test suite** — the required check on each pull request is what tests
  a release → same file, §4.
- **The repository is public:** nothing committed names a secret, a private repository or an internal tool → same
  file, §6.
- **Status is never written into this skill.** The version is `<Version>` in the Cli project, releases are
  `gh release list`, a pull request's state is `gh pr checks`, test counts come from running the suite.

## Workflow

1. Identify the area the question touches and read that reference (see the file map).
2. Find the canonical example and the recipe before writing anything new — the repo has an established pattern for
   nearly everything, and matching it is cheaper than inventing.
3. Apply [references/testing-and-conventions.md](references/testing-and-conventions.md). Its rules are load-bearing,
   not style preferences.
4. Check the authority order whenever this skill and a document disagree.

## What a change must satisfy

- Core holds the rule, once; Cli only wires it; nothing above the platform seam asks the OS.
- New behaviour has a test that goes **red when the change is reverted**, and the whole suite passes on Windows and
  on Linux.
- Exit codes, `--json` documents and named records hold on every exit, early and refused ones included.
- Help, README, `docs/architecture.md` and `docs/releasing.md` say the new behaviour in the same commit.
- Every review finding is fixed at its cause.

The step-by-step version, recipe by recipe, is [references/recipes.md](references/recipes.md).

## File map

- Read [references/repo-map.md](references/repo-map.md) for the projects, where each concern lives in Core and Cli,
  the layering rules, the build and toolchain, how to run the suite (filters, slow classes, build tools, Linux from
  Windows), canonical examples, and which document says what.
- Read [references/testing-and-conventions.md](references/testing-and-conventions.md) before writing any test or any
  code: the strict-test rules, the shared test helpers and doubles, platform skips, documents held to the code, and
  the coding conventions.
- Read [references/recipes.md](references/recipes.md) before adding a command, a config key, an exit code, a verdict
  or a help topic, changing what `init` writes or what a command records, or changing a workflow.
- Read [references/release-and-ci.md](references/release-and-ci.md) for who merges and releases, the workflows, the
  test matrix, the required check, versions and channels, the security posture, and how workflows are tested.

## Failure modes this skill exists to prevent

- **Green here, red in CI**: running only the tests that look related, or only on one system.
- **A test that passes both ways.** Verify red-on-revert directly.
- **A second owner for a rule** — a copied check, a hand list beside a generated one — that drifts from the first.
- **Help or docs that promise what the code no longer does**, because a sibling sentence was not searched for.
- **Branching on the OS above the seam**, which no test on one platform catches.
- **Trusting this skill over the docs, the help or the tests** when they disagree.
