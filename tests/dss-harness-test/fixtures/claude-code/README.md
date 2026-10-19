# The Claude Code probe's fixtures

What the agent-clients slices build on: the recorded protocol of a live `claude`, streamed
both ways, as part 03 of `.plans` (the agent clients) spells the probe. The suite's
no-network tests read these files; nothing here runs anything live.

## What is here

- `shapes.jsonl` under each `<channel>/` — the curated recording of one probe of that channel
  (`stable` = the npm `stable` dist-tag, `latest` = `latest`), sanitized by the curation rules
  below. The line kinds, their members and their shapes are exactly what the probe saw.
- `probe.md` (here, beside this README) — the point-by-point answers of both channels, in the
  plan's own words.

## What is not here, and why

The live probe (`ClaudeCodeProbeTests`) writes its raw recordings under each channel's `raw/` as
it runs — `stream.jsonl`, `control.jsonl`, `claude-stderr.log`, `findings.json`,
`silent-host.json` and the two files it launches the child with. A raw transcript holds
the machine's own slash commands, agents, skills and absolute paths, and this repository
is public, so that whole directory is git-ignored (see `.gitignore`) and never committed. What
the suite needs from them is curated into `shapes.jsonl` by hand after a run, by the curation
rules below.

## Curation rules

A curated recording keeps nothing of the machine or the account that made it. Before a new
`shapes.jsonl` is committed:

- The person's own slash commands, agents, skills and plugins are `<elided: the person's own>`,
  the machine's memory, socket and shell paths `<elided>`, the initialize answer's commands,
  agents, output styles, models and account one `elided` member, and the scratch repository's
  path `<scratch>`.
- Every uuid and session id is a stand-in, `00000000-0000-4000-8000-` and twelve digits counting
  from one in the order each first appears.
- Every id a server gave — a request's (`req_…`), a message's (`msg_…`), a tool call's (`toolu_…`,
  and a server tool's `srvtoolu_…`) — is its prefix and 24 digits counting from one, for each
  prefix, in the order each first appears; the same stand-in wherever the recording names the same
  id, a member's name included (`wire_tool_inputs`), so a call, its ask, its result and its denial
  still pair up.
- The account's own members of `rate_limit_info` — `overageStatus` and `overageDisabledReason` —
  are `<elided: the account's own>`; `status`, `rateLimitType` and `isUsingOverage` stay as the
  event's shape.
- Timestamps, durations, token counts and costs stay as recorded: they say nothing of who ran the
  probe.

`ClaudeCodeProtocolTests` holds the committed recordings to the ids, the uuids and the rate limit's
members of these rules, so a recording curated without them is refused by the suite.

## Regenerating

Set `DSSHARNESS_TESTS_LIVE_CLAUDE` to the stable channel's scratch `claude.exe` (each
version installed into its own scratch npm prefix, never globally: `npm install --prefix
<prefix> @anthropic-ai/claude-code@stable`), then run the probe test. It uses the person's
login and runs a live program: it is skipped unless the variable is set, and the engine
never starts it. **It is a local, person-run test and nothing else**: a continuous-integration
runner never sets the variable (it has no login and must not), so the probe is skipped there
by design — the suite's coverage of this protocol is the committed fixtures and the fake
`claude` reading them, which are offline. `DSSHARNESS_TESTS_LIVE_CLAUDE_SMOKE` shortens the
probe to the initialize handshake and one turn, for shaping the protocol by hand.
