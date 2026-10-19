# The Claude Code probe — what it settled, point by point

The probe of part 03's slice 12, run 2026-10-18 against the npm `stable` channel (2.1.292) and the
`latest` channel (2.1.302), each from its own scratch npm prefix through the person's login, in a
scratch repository outside any checkout. The recordings are curated into `shapes.jsonl` beside this
file; the raw transcripts stay on the machine that made them (the fixtures README says why). The
probe's own driver is `ClaudeCodeProbeTests`, which the suite skips unless the machine opts in —
the live probe is a person's, never a continuous-integration runner's, for a runner has no login.

What the committed recordings hold, both channels, is: nine turns each - a read under an ask rule
answered allow; a plain second message; a counting turn ended by the interrupt and a message after
it; a write allowed; a write denied; a command allowed; a web search allowed; a read whose ask was
never answered. Where an answer below says "the morning's expired-login runs", it rests on those
uncommitted runs (the login was expired then; the person renewed it and the committed recordings
followed) and is marked as such.

## The control protocol, both ways

- **The envelope nests the method inside `request.subtype`**, in both directions. A request is
  `{"type":"control_request","request_id":"<uuid>","request":{"subtype":"initialize",...}}`; an
  answer is `{"type":"control_response","response":{"subtype":"success","request_id":"<the
  request's id>","response":{...}}}` — the answer's id rides inside `response`, and there is no
  `response_to`, no `method`, no top-level `params`. A flat shape is not heard at all: the child
  answers `Unsupported control request subtype: undefined` or `Missing request on
  control_request`, and for a permission ask whose answer came in that shape the ask fails with
  `AbortError: Tool permission stream closed before response received` — and the permission stream
  stays closed for the session's remaining asks. The fake `claude` does the same to a flat answer:
  it is the same silence.
- **`system/init` arrives before anything is asked, and again before every turn** — nine turns in
  each recording, nine init lines, the same session and the same fields each time. It carries:
  tools, mcp servers, model, permissionMode, slash commands, terminal slash commands,
  `apiKeySource`, `claude_code_version`, output style, agents, skills, plugins, `capabilities`,
  uuid, memory paths, socket path, effort state. The initialize handshake answers with the slash
  commands, agents, output styles, models and account.
- **The ask** — `can_use_tool` — carries `tool_name`, `display_name`, `input`, `description`,
  `decision_reason_type` (`rule` where a settings rule matched, `other` with a `matched_ask_rule`
  for the command on `latest`), and the `tool_use_id` of the held call. The host's answer carries
  `behavior` (`allow` with `updatedInput`, or `deny` with a `message`), nested as above.
- **The stream**: `stream_event` lines carrying the wire's own events (`message_start`,
  `content_block_start` of `tool_use`/`text`/`thinking`, `content_block_delta` of
  `text_delta`/`input_json_delta`/`thinking_delta`, `content_block_stop`, `message_delta`,
  `message_stop`), whole `assistant` messages with the full provider message inside (model, usage,
  content blocks), `user` lines for replayed messages (`isReplay: true`) and for tool results
  (`tool_result` content with `is_error`), `system`/`subtype:status` lines (`status: requesting`),
  `rate_limit_event` lines, and one `result` line a turn. The committed recordings hold two
  terminal reasons — `completed` and `aborted_streaming` — with `subtype: success` on both (and
  `error_during_execution` beside the interrupted one); the result line's members: `num_turns`,
  `usage` (the plan's AgentUsage members all present: input/output/cache read/cache write, and
  `server_tool_use`'s `web_search_requests`/`web_fetch_requests`), `modelUsage` (per model),
  `permission_denials` (each entry `tool_name`, `tool_use_id`, `tool_input` — counted),
  `total_cost_usd`, `queued_turn_count`, `result_index`. An assistant line carrying
  `error`/`is_api_error_message` was seen only in the morning's expired-login runs (a
  provider-failed message, `<synthetic>` as its model, `terminal_reason: api_error` on its
  result) — not in the committed recordings, which met no provider failure.

## The five points of the handoff turn, answered

- **(a) A second user message after the first turn's result is answered on the same stream**:
  the read turn ended `completed` with `{"answer":"alpha"}`, the next message was answered
  `completed` with `{"answer":"Blue"}` — same session, same stream, no re-launch.
- **(b) The interrupt ends the turn under way**: the request
  `{"subtype":"interrupt","cancel_queued":true}` (in the recorded envelope — a host-side write, so
  the committed child stream holds its acknowledgement, not the request itself) is acknowledged by
  a `control_response`, and the counting turn's result arrived `terminal_reason:
  "aborted_streaming"` with `error_during_execution` beside it — a terminal reason of its own, the
  turn's part-streamed text left behind.
- **(c) A further message after the interrupt is answered, holding what went before**: asked
  "which file did you read first in this session?", the answer came `{"answer":"a.txt"}` — the
  file read before the interrupt, `completed`.
- **(d) The four kinds of call under the ask rules**: a read, a write, a command and a web search
  were each asked about and each allowed one ran (the file read; `written.txt` created; the
  command's output `marker=settings` recorded; web results returned), and the denied write did
  not run — its tool result is the denial's own message (`denied by the probe`), the file never
  existed, and the turn's result counted `permission_denials: 1`, the entry naming the tool, the
  call and the input.
- **(e) A call with the host silent runs nothing**: the ask for the read of `probe.txt` was never
  answered; through the 45-second window no result and no tool came. The ask itself then failed by
  the child's own timeout (`Tool permission request failed: AbortError: Tool permission stream
  closed before response received`), the model answered around the failure, and the turn ended
  `completed` with the file unread and a denial counted — nothing ran in the silence.

## The flags

- The launch's argument list whole (the plan's Permissions passage): accepted by both versions;
  `--session-id` must be a true UUID (a dotted name is refused at start), `--effort` exists and
  `low` was taken, `--json-schema` adds a `StructuredOutput` tool to `system/init`'s tools,
  `--no-session-persistence` and `DISABLE_COMPACT` in the settings' `env` are accepted under
  streamed input.
- **No session file is written**: under `--no-session-persistence` with streamed input and no
  `-p`, the project's session directory held no transcript of either run.
- `permissions.ask` in `--settings` is an **array** of rule strings; an object there is refused
  and the whole settings file does not load ("These settings files did not load... including
  hooks and permission rules").
- A schema-enforcing turn: the model is nudged by a harness-side `user` message
  (`[structured-output-enforce] You MUST call the StructuredOutput tool...`) and the turn's
  result arrives as the envelope (`{"answer":"..."}`), on the first turn and on later turns
  alike — turn two answered `{"answer":"Blue"}` through it.
- `--model` with an alias resolves and is named: `sonnet` resolved to `claude-sonnet-5-5`, named
  in `system/init` (all nine) and in every assistant message, on both channels. A model's **id**
  was not run — the engine's own case ("this tool passes a model's id and no alias") — and the
  facts list's four built-in ids on each version were not either; both owe a regeneration.
- The `env` of `--settings` wins over the **process's** environment for a name both set: the
  child was launched with `PROBE_MARKER=process` in its environment and `PROBE_MARKER=settings`
  in the settings' `env`; the command the agent ran printed `marker=settings`. Against the user's
  own settings `env` it was not measured (that would write the person's settings).
- `total_cost_usd` under the login: a real, small number per turn — 0.0183 on the stable read
  turn, 0.0420 on latest's, 0.0452 on latest's plain second turn — every result line of both
  recordings nonzero. The morning's expired-login runs said exactly 0 on every turn that failed
  before the provider; no committed recording carries a 0. What the number stands for under a
  subscription's rates stays with the prices slice.

## What the init line says of the machine

`apiKeySource` says `none` under a working OAuth login (it names an API key's source, not the
login's; the expired login said `none` too). What it says under a key from the slot was not
measured — no key was in the environment, by design. The tools' exact names, both versions:
`Bash, Edit, Glob, Grep, NotebookEdit, Read, StructuredOutput, WebFetch, WebSearch, Write`. The
user's own skills, agents and slash commands load under `--setting-sources user` (18 skills on
this machine). `capabilities` advertise the interrupt protocol (`interrupt_receipt_v1`,
`interrupt_cancel_queued_v1`, `interrupt_send_now_v1`), message lifecycle (`msg_lifecycle_v1`)
and more, differing slightly between the channels (`latest` adds `request_marker_lists_v1` and
`mcp_call_result_not_for_model_v1`).

## Not settled by this run

- **The oldest version taken — 2.1.287, pinned 2026-10-12 — was not probed.** The protocol held
  identically on 2.1.292 and 2.1.302, and nothing observed forces a version above the stable; but
  whether it holds on the floor version, and the tool names on it, are unknown until a
  regeneration names it.
- `--model` with an id (the engine's own case), and each of the four built-in ids on each
  version.
- The other `result` subtypes and terminal reasons (`error_max_turns`, `error_during_execution`
  outside an interrupt, `prompt_too_long`, `budget_exhausted` and the rest): none was driven; the
  committed recordings hold `completed` and `aborted_streaming` only.
- The `-p` half of "with `-p` and without": only the without-`-p` mode ran (checked by hand once,
  headless, before the login was renewed; not recorded).
- Whether `--permission-prompt-tool stdio`, which only the source shows, is needed beside the
  documented `--permission-prompts host`: every launch passed both, and none was started with
  one alone, so what either does without the other is unknown.
- "Or after it has gone", and what becomes of a waiting call when the host dies, closes the input
  or answers `error`: the host silent was measured for 45 seconds; the host gone (dead, or its
  input closed) was not, and no ask was answered `error` — the host answered `allow`, `deny` or
  nothing.
- Whether the `env` of `--settings` wins over the user's own `env` for a name both set: what was
  measured ("The flags" above) is the `env` of `--settings` against the **process's**
  environment, and the user's own settings were never given the name, so their `env` was not set
  beside it.
- What `system/init`'s `apiKeySource` says under each way of signing in: only the login ran, and
  said `none`; under a key from the slot it was not measured, no key being in the environment by
  design.
- "What a full context says": no turn filled the context; `DISABLE_COMPACT` is shown accepted,
  never shown to hold.
- A protected path denied by the built-in rules (no write outside the worktree was asked for);
  the host's own `deny` is recorded, the built-in path is not.
- Whether an empty `fallbackModel` in `--settings` takes the user's chain away: the setting was
  sent and accepted, but no fallback was taken in either run.
- `CLAUDE.md` of the working directory still read under `--setting-sources user`, and that none
  of the worktree's skills is loaded: the scratch repository had a `CLAUDE.md` but no turn asked
  the model to quote it, and it had no skills of its own to load; a repository that has both owes
  the regeneration one more turn.
- The read-only tools plural: only `Read` reached an ask; `Grep` and `Glob` under their ask rules
  were never asked.
