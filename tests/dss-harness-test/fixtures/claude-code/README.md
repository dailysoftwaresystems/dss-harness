# The Claude Code probe's fixtures

What the agent-clients slices build on: the recorded protocol of a live `claude`, streamed
both ways, as part 03 of `.plans` (the agent clients) spells the probe. The suite's
no-network tests read these files; nothing here runs anything live.

## What is here

One folder a channel, each the curated recordings of one probe of it: `oldest` (2.1.287, the
oldest version this tool takes), `stable` (the npm `stable` dist-tag) and `latest` (`latest`).
In each:

- `shapes.jsonl` — the scenario: one launch, sixteen turns (a read, a second message, an
  interrupt and a message after it, allow and deny across write, command and web, `Grep` and
  `Glob`, the worktree's `CLAUDE.md` and skills, two protected paths and one outside the working
  directory, an ask answered `error`, a silent host).
- One `<launch>.jsonl` for each further launch of the probe: `max-turns`, `budget` and
  `unknown-model` (the other results), `full-context`, `print`, `print-budget` and
  `print-prompts-host-only` (the `-p` half), `input-closed` and `host-dead` (the host gone),
  `prompt-tool-only`, `prompts-host-only` and `no-prompt-flag` (which flag sends an ask),
  `fallback-below` and `fallback-emptied`, `env-project`, `env-user` and
  `env-desktop-entrypoint`, `model-<id>` for each built-in model id but the scenario's,
  `mcp-message` (the engine's tool server over the control channel: an MCP server of type `sdk`,
  its connection, a call allowed and one denied) and `deny-rules` (a read, a search, a write and a
  command Claude Code's own deny rules refuse) - the two recorded 2026-10-26 on 2.1.287, the
  `stable` tag's 2.1.308 and the `latest` tag's 2.1.310, the launches of another day.
- `findings.jsonl` — what the probe found beside each launch's stream, one line a launch: files
  written or not, whether the child exited and with what, the session file it looked for, a
  hook's output.

The line kinds, their members and their shapes are exactly what the probe saw, curated by the
rules below. `probe.md` (here, beside this README) is the point-by-point answers, in the plan's
own words.

## What is not here, and why

The live probe (`ClaudeCodeProbeTests`) writes its raw recordings under each channel's
`raw/<launch>/` as it runs — `stream.jsonl`, `control.jsonl`, `claude-stderr.log`,
`findings.json` and the two files it launches the child with. A raw transcript holds the
machine's own slash commands, agents, skills and absolute paths, and this repository is public,
so every `raw/` directory is git-ignored (see `.gitignore`) and never committed. What the suite
needs from them is curated by hand after a run, by the curation rules below.

## Curation rules

A curated recording keeps nothing of the machine or the account that made it. Before a new one
is committed:

- The person's own slash commands, agents, skills and plugins are one `<elided: the person's
  own>` entry — the probe's own (`probe-…`) are kept, for they are what a point reads — the
  machine's memory, socket, shell and scratchpad paths `<elided>`, the initialize answer's
  commands, agents, output styles, models and account one `elided` member, the scratch
  repository's path `<scratch>`, the directory beside it `<outside>` (under any spelling of the
  home directory, `~` included), and the scratch Claude Code directory `<scratch config>`.
- Every uuid and session id is a stand-in, `00000000-0000-4000-8000-` and twelve digits counting
  from one in the order each first appears.
- Every id a server gave — a request's (`req_…`), a message's (`msg_…`), a tool call's (`toolu_…`,
  and a server tool's `srvtoolu_…`) — is its prefix and 24 digits counting from one, for each
  prefix, in the order each first appears; the same stand-in wherever the recording names the same
  id, a member's name included (`wire_tool_inputs`), so a call, its ask, its result and its denial
  still pair up. An id is its prefix and 16 or more letters and digits: a word of the protocol such
  as `msg_lifecycle_v1` is kept as written.
- The account's own members of `rate_limit_info` — `overageStatus`, `overageDisabledReason`,
  `overageEnabled`, `unifiedWindows` and `utilization` (its usage), each where the version writes
  it — are `<elided: the account's own>`; `status`, `rateLimitType` and `isUsingOverage` stay as the event's shape.
- A `stream_event` whose event shape (its event type, and its content block's or delta's type)
  already appeared in the file is dropped: the first of each shape stays, whole. The probe's own
  large files, read to fill the context, are `<elided: the probe's big file, N lines>`.
- `findings.jsonl` keeps each launch's findings without the argument list, the session id and
  the names of the variables the launch cleared, its paths and ids under the stream's own rules.
- Timestamps, durations, token counts and costs stay as recorded: they say nothing of who ran the
  probe.

`ClaudeCodeProtocolTests` holds every committed recording to the ids, the uuids, the paths and the
rate limit's members of these rules, so a recording curated without them is refused by the suite.

## Regenerating

Install each channel into a scratch npm prefix of its own under one root, never globally:
`npm install --prefix <root>/<channel> @anthropic-ai/claude-code@<version>` for `oldest`
(2.1.287), `stable` and `latest`. Set `DSSHARNESS_TESTS_LIVE_CLAUDE` to the stable channel's
`claude` (`<root>/stable/node_modules/@anthropic-ai/claude-code/bin/claude.exe` on Windows); the
other channels are looked for beside it, and `DSSHARNESS_TESTS_LIVE_CLAUDE_CHANNELS` (for example
`oldest,latest`) narrows a run. Each of the probe's points is a test of its own, run against every
channel at once. Each launch is made as a person's terminal makes it: a Claude Code session's own
variables (`CLAUDECODE`, `CLAUDE_CODE_*`, `ANTHROPIC_*` and the like), which a test run from inside
one inherits, are cleared from its environment, since one of them changes what the probe measures.

The probe uses the person's login and runs a live program: it is skipped unless the variable is
set, and the engine never starts it. **It is a local, person-run test and nothing else**: a
continuous-integration runner never sets the variable (it has no login and must not), so the
probe is skipped there by design — the suite's coverage of this protocol is the committed fixtures
and the fake `claude` reading them, which are offline. It never writes the person's own settings
or login: where a point needs a settings layer below the launch's, it writes the scratch
repository's project settings or a scratch Claude Code directory.
