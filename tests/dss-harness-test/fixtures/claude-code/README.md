# The Claude Code probe's fixtures

What the agent-clients slices build on: the recorded protocol of a live `claude`, streamed
both ways, as part 03 of `.plans` (the agent clients) spells the probe. The suite's
no-network tests read these files; nothing here runs anything live.

## What is here

- `shapes.jsonl` under each `<channel>/` — the curated recording of one probe of that channel
  (`stable` = the npm `stable` dist-tag, `latest` = `latest`), sanitized: the person's own
  commands, agents, skills, plugins and paths are elided, and every uuid, session id and
  timestamp is the recorded one or a stable stand-in. The line kinds, their members and
  their shapes are exactly what the probe saw.
- `probe.md` (here, beside this README) — the point-by-point answers of both channels, in the
  plan's own words.

## What is not here, and why

The live probe (`ClaudeCodeProbeTests`) writes its raw recordings under each channel's `raw/` as
it runs — `stream.jsonl`, `control.jsonl`, `claude-stderr.log`, `findings.json`,
`silent-host.json` and the two files it launches the child with. A raw transcript holds
the machine's own slash commands, agents, skills and absolute paths, and this repository
is public, so that whole directory is git-ignored (see `.gitignore`) and never committed. What
the suite needs from them is curated into `shapes.jsonl` by hand after a run.

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
