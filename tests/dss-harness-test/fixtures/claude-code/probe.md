# The Claude Code probe — what it settled, point by point

The probe of part 03's slice 12. It first ran on 2026-10-18 against the npm `stable` channel
(2.1.292) and `latest` (2.1.302); the recordings committed now are the run of 2026-10-19 against
the oldest version taken (2.1.287, folder `oldest`), `stable` (2.1.292) and `latest` (2.1.303), each
from its own scratch npm prefix through the person's login, every launch in a scratch repository
of its own outside any checkout. The recordings are curated beside this file (the README says
which file is which launch, and how they are made); the raw transcripts stay on the machine that
made them. The probe's driver is `ClaudeCodeProbeTests`, which the suite skips unless the machine
opts in — the live probe is a person's, never a continuous-integration runner's, for a runner has
no login.

Every answer below held identically on all three versions unless it names one. Each launch was
made as a person's terminal makes it: the probe ran inside a Claude Code session, and that
session's own variables were cleared from every launch's environment (one of them changes an
answer — "The env of `--settings`" below).

## On which versions the protocol holds, and the oldest version taken

**The control protocol holds identically on 2.1.287, 2.1.292 and 2.1.303**: the same envelope
both ways, the same initialize answer, `system/init` before every turn, the same asks and
answers, the same results and terminal reasons, and the same tool names — `Bash, Edit, Glob, Grep,
NotebookEdit, Read, StructuredOutput, WebFetch, WebSearch, Write` — on each. Nothing observed
forces a version above 2.1.287: **the oldest Claude Code taken stays 2.1.287.** What differs is
additive: 2.1.303's `system/init` adds `original_cwd` and `additional_directories`, its
`capabilities` add `request_marker_lists_v1` and `mcp_call_result_not_for_model_v1`, and its asks
add `decision_reason_rule_source` and `server_policy_ask_only`.

## The control protocol, both ways

- **The envelope nests the method inside `request.subtype`**, in both directions. A request is
  `{"type":"control_request","request_id":"<uuid>","request":{"subtype":"initialize",...}}`; an
  answer is `{"type":"control_response","response":{"subtype":"success","request_id":"<the
  request's id>","response":{...}}}` — the answer's id rides inside `response`, and there is no
  `response_to`, no `method`, no top-level `params`. A flat shape is not heard at all: the child
  answers `Unsupported control request subtype: undefined` or `Missing request on
  control_request`, and for a permission ask whose answer came in that shape the ask fails with
  `AbortError: Tool permission stream closed before response received` — and the permission stream
  stays closed for the session's remaining asks (the run of 2026-10-18). The fake `claude` does
  the same to a flat answer: it is the same silence. The child repeats each answer the host wrote
  on its own stream, as a `control_response` line.
- **`system/init` arrives before anything is asked, and again before every turn** — sixteen
  turns in each scenario, sixteen init lines, the same session and the same fields each time. It
  carries: tools, mcp servers, model, permissionMode, slash commands, terminal slash commands,
  `apiKeySource`, `claude_code_version`, output style, agents, skills, plugins, `capabilities`,
  uuid, memory paths, socket path, effort state. The initialize handshake answers with the slash
  commands, agents, output styles, models and account.
- **The ask** — `can_use_tool` — carries `tool_name`, `display_name`, `input`, `description`, the
  `tool_use_id` of the held call, and why it was asked: `decision_reason_type` `rule` for a read,
  a search, `Grep` and `Glob` (2.1.303 adds `decision_reason_rule_source: flagSettings` and
  `server_policy_ask_only`), `other` with a
  `decision_reason` for a command whose variable cannot be checked, `safetyCheck` and
  `workingDir` for protected paths (below); a write under its ask rule names no
  `decision_reason_type`, only `matched_ask_rule: {"source":"flagSettings","tool_name":"Write"}`.
  The host's answer carries `behavior` (`allow` with `updatedInput`, or `deny` with a `message`),
  nested as above, or is an error (below).
- **The stream**: `stream_event` lines carrying the wire's own events (`message_start`,
  `content_block_start` of `tool_use`/`text`/`thinking`, `content_block_delta` of
  `text_delta`/`input_json_delta`/`thinking_delta`, `content_block_stop`, `message_delta`,
  `message_stop`), whole `assistant` messages with the full provider message inside (model, usage,
  content blocks), `user` lines for replayed messages (`isReplay: true`) and for tool results
  (`tool_result` content with `is_error`), `system` lines (`status`, `post_turn_summary`,
  `task_summary`, `thinking_tokens`, and `permission_denied` and `model_fallback` below),
  `rate_limit_event` lines, and one `result` line a turn. The result line's members: `subtype`,
  `terminal_reason`, `is_error`, `num_turns`, `usage` (the plan's AgentUsage members all present:
  input/output/cache read/cache write, and `server_tool_use`'s
  `web_search_requests`/`web_fetch_requests`), `modelUsage` (per model, with the window it assumed,
  `contextWindow`), `permission_denials` (each entry `tool_name`, `tool_use_id`, `tool_input` —
  counted), `total_cost_usd`, `queued_turn_count`, `result_index`. **A `success` carries `result`
  and an error subtype carries `errors` and no `result`**; a completed turn under the schema
  carries its envelope twice — as text in `result` (`"{\"answer\":\"alpha\"}"`) and as that JSON
  in `structured_output` (`{"answer":"alpha"}`).

## The five points of the handoff turn, answered

- **(a) A second user message after the first turn's result is answered on the same stream**:
  the read turn ended `completed` with `{"answer":"alpha"}`, the next message was answered
  `completed` with `{"answer":"Blue"}` — same session, same stream, no re-launch.
- **(b) The interrupt ends the turn under way**: the request
  `{"subtype":"interrupt","cancel_queued":true}` (a host-side write, so the committed child stream
  holds its acknowledgement, not the request itself) is acknowledged by a `control_response`, and
  the counting turn's result arrived `subtype: error_during_execution`, `is_error: true`,
  `terminal_reason: aborted_streaming`, its diagnostic in `errors` and no `result` — the turn's
  part-streamed text left behind.
- **(c) A further message after the interrupt is answered, holding what went before**: asked
  "which file did you read first in this session?", the answer came `{"answer":"a.txt"}` — the
  file read before the interrupt, `completed`.
- **(d) The kinds of call under the ask rules**: a read, a write, a command, a web search, and
  the read-only `Grep` and `Glob`, were each asked about and each allowed one ran (the file read;
  `written.txt` created; the command's output `marker=settings` recorded; web results returned;
  `Grep` found `a.txt`; `Glob` listed the files), and the denied write did not run — its tool
  result is the denial's own message (`denied by the probe`), the file never existed, and the
  turn's result counted `permission_denials: 1`, the entry naming the tool, the call and the
  input.
- **(e) A call with the host silent runs nothing**: the ask for the read of `probe.txt` was never
  answered; through the 45-second window no result and no tool came. When the probe then closed
  the host's input, the ask failed (`Tool permission request failed: AbortError: Tool permission
  stream closed before response received`), the model answered around the failure, and the turn
  ended `completed` with the file unread and a denial counted — nothing ran in the silence. (The
  first run read that failure as the child's own timeout; it is the input closing, as "The host
  gone" below shows. No ask failed by itself within 45 seconds.)

## The flags

- The launch's argument list whole (the plan's Permissions passage): accepted by all three
  versions; `--session-id` must be a true UUID (a dotted name is refused at start), `--effort low`
  is taken, `--json-schema` adds a `StructuredOutput` tool to `system/init`'s tools,
  `--no-session-persistence` and `DISABLE_COMPACT` in the settings' `env` are accepted under
  streamed input.
- **`--permission-prompt-tool stdio` is what sends an ask to the host; `--permission-prompts host`
  alone does not.** Launched with `--permission-prompt-tool stdio` alone, the read was asked and
  ran; with `--permission-prompts host` alone, with neither, and with `-p` and
  `--permission-prompts host` alone, nothing was asked: the call was denied on the spot, a
  `system/permission_denied` line saying so (`decision_reason_type: rule`, `Claude requested
  permissions to use Read, but you haven't granted it yet.`), the tool result the same words, the
  denial counted. The launch keeps both flags; the first is the one that does the work.
- **`--model` with a model's id runs that model as written, and the stream names it**:
  `claude-fable-5-1`, `claude-opus-5-5`, `claude-sonnet-5-5` (the scenario's) and
  `claude-haiku-5-5` each ran on each version, named in every `system/init` and in every
  assistant message. An id the provider does not know is named in `system/init` as given, and
  every turn ends in an API error (below).
- **No session file is written**: under `--no-session-persistence`, with `-p` and without, no
  launch of the 21 on any version left `<session id>.jsonl` under the projects directory. Each
  launch does leave an empty `memory` directory there, under the working directory's name.
- `--max-turns` and `--max-budget-usd` hold without `-p` (the help says "only works with
  --print"): see the results below.
- `permissions.ask` in `--settings` is an **array** of rule strings; an object there is refused
  and the whole settings file does not load (the run of 2026-10-18).
- A schema-enforcing turn: the model is nudged by a harness-side `user` message
  (`[structured-output-enforce] You MUST call the StructuredOutput tool...`) and the turn's
  result arrives as the envelope, on the first turn and on later turns alike.
- `total_cost_usd` under the login: a real, small number, the launch's running total — every
  result line a launch completed is nonzero, and a turn that never reached the provider adds
  nothing (an unknown model's results say exactly 0). What the number stands for under a
  subscription's rates stays with the prices slice.

## The `-p` half

**With `-p` the launch speaks the same protocol streamed both ways**: the initialize answer,
`system/init`, the ask and its answer, the same result line; it does not end after its first
result while its input is open (fifteen seconds later it was still there), and a second message
was answered on the same stream. `--max-budget-usd` ends its turn the same way, and
`--permission-prompts host` alone sends nothing to the host there either. No session file.

## The other results and terminal reasons

- **The turn limit**: under `--max-turns 1`, a turn whose allowed read ran and needed one more
  request ended `subtype: error_max_turns`, `terminal_reason: max_turns`, `is_error: true`,
  `errors: ["Reached maximum number of turns (1)"]`, no `result`; the write it led to never ran.
- **The budget**: under `--max-budget-usd 0.0001`, the first request spent past it while its read
  was being asked; the turn ended `subtype: error_max_budget_usd`, `terminal_reason:
  budget_exhausted`, `errors: ["Reached maximum budget ($0.0001)"]` **with the ask still waiting
  and never answered for** — the read never ran, and the host's late allow was ignored. A further
  message ended the same way at once, without a request. A result can therefore arrive while an
  ask is outstanding.
- **An error**: a model id the provider does not know (`claude-no-such-model-0-0`) ended each turn
  in a message of Claude Code's own — model `<synthetic>`, `error: model_not_found`,
  `is_api_error_message: true`, "There's an issue with the selected model (…)" — and a result
  `subtype: success`, `is_error: true`, `terminal_reason: api_error`, the same words in `result`,
  `total_cost_usd: 0`. The same shape with `error: authentication_failed` ("Not logged in · Please
  run /login") is what a launch with no credential got.
- With the interrupt's `aborted_streaming` and `completed`, the recordings hold `max_turns`,
  `budget_exhausted`, `api_error` and `blocking_limit` (below). `prompt_too_long`,
  `aborted_tools` and `error_max_structured_output_retries` were not met.

## A full context

**A full context ends the turn at `blocking_limit` and is not compacted.** The window was lowered
for that launch alone by the documented `CLAUDE_CODE_MAX_CONTEXT_TOKENS=50000` (for a model Claude
Code knows it holds only beside `DISABLE_COMPACT`, which the launch's settings set; `modelUsage`
then names `contextWindow: 50000`). Each turn read a large file: the first completed; in the second
the read was asked, allowed and ran, and the next request was refused before it was sent — a
`<synthetic>` assistant message `Prompt is too long` with `error: invalid_request`, and a result
`subtype: success`, `is_error: true`, `terminal_reason: blocking_limit`, `result: "Prompt is too
long"`. A message after it ended the same way at once. No compaction line came; the session stays
full.

## The host gone, or answering `error`

- **The host's input closed while a call waits**: the waiting ask failed (`Tool permission request
  failed: AbortError: Tool permission stream closed before response received`), nothing ran, the
  model tried once more and that call failed too (`AbortError: Stream closed`), the turn ended
  `completed` with both denials counted, and the process then exited 0 by itself, within ten
  seconds of the close.
- **The host dead** — its input and its reading end both closed at the ask, no answer given: the
  stream ends at the ask, the write never ran, and the process exited 0 by itself within fifteen
  seconds.
- **An ask the host answers `error`** —
  `{"type":"control_response","response":{"subtype":"error","request_id":...,"error":"..."}}` —
  fails the call with the host's own words (`Tool permission request failed: Error: <the
  error>`), runs nothing, and is counted by the turn's result as a denial; the turn completes.

## Protected paths and the built-in rules

**A path Claude Code protects is asked of the host, not refused before it**: a write into `.git`
and into the worktree's `.claude/settings.local.json` came as asks with `decision_reason_type:
safetyCheck` ("… which is a sensitive file", `classifier_approvable: true`), a write outside the
working directory with `decision_reason_type: workingDir` ("Path is outside allowed working
directories"), and the host's allow ran each. No built-in rule denied anything the probe asked
for: in the default mode under ask rules, the host decides every call, these included.

## Settings: the fallback chain, the env, and what the worktree gives

- **An empty `fallbackModel` in `--settings` takes away the chain a lower settings file names.**
  With an unknown model and a lower file's `"fallbackModel": ["claude-haiku-5-5"]` — the scratch
  repository's project settings, read beside the user's, standing in for the user's own file,
  which the probe never writes — a launch whose settings named no fallback failed over
  (`system/model_fallback`, `trigger: model_not_found`, `original_model`, `fallback_model`, the
  turn answered by `claude-haiku-5-5`); the same launch with `"fallbackModel": []` did not, and
  ended in the API error. That is the documented rule — the whole chain comes from the
  highest-precedence file that sets it — and the user's file sits below `--settings` as the
  project's does.
- **The `env` of `--settings` wins for a name another layer also sets**: over the process's own
  environment (the scenario's command printed `marker=settings` though the process set
  `PROBE_MARKER=process`), over the project's settings (`marker=settings lower=project`), and over
  **the user's own** — a scratch Claude Code directory whose `settings.json` set
  `PROBE_MARKER=user`, where a hook of the launch printed `marker=settings lower=user`. A name only
  a lower layer sets still reaches the session.
- **But a launch that inherits a desktop app's `CLAUDE_CODE_ENTRYPOINT=claude-desktop` keeps the
  process's value**: the same command printed `marker=process`. The probe found it by running
  inside such a session (its first pass, discarded, said `process` everywhere); of that session's
  variables, `CLAUDE_CODE_ENTRYPOINT` alone does it. A launch's environment must not carry a host
  Claude Code session's variables if its `env` is to hold.
- **Under `--setting-sources user` nothing of the worktree is read**: asked without a tool, the
  model found no line of the worktree's `CLAUDE.md` in its context and no skill
  `probe-worktree-skill` (`{"answer":"none; no"}`), and `system/init` listed no worktree skill;
  with `project` among the sources it lists it. A skill of the user's own directory is listed —
  under the person's login, as `<elided: the person's own>`, and from a scratch directory as
  `probe-user-skill` — while the launch's tools hold no skill tool, so none can be invoked.

## What the init line says of the machine

`apiKeySource` says `none` under the person's working login on every version, and says `none`
too with no credential at all (a scratch Claude Code directory, where every turn ends `Not logged
in`): it names an API key's source, and does not tell a login from no credential. The user's
own skills, agents and slash commands load under `--setting-sources user`. `capabilities`
advertise the interrupt protocol (`interrupt_receipt_v1`, `interrupt_cancel_queued_v1`,
`interrupt_send_now_v1`), message lifecycle (`msg_lifecycle_v1`) and more.

## Not settled by this run

- **What `apiKeySource` says under a key from the slot**: no key was in any launch's
  environment, and none was made for the probe — the one sign-in available without a new
  credential is the person's login, which says `none`.
- **The user's own fallback chain, live**: the empty `fallbackModel` was shown to take away a
  lower file's chain with the project's settings standing in for the user's; a chain in the
  user's own `settings.json` was not, since that is the person's file, and a scratch Claude Code
  directory has no login under which a request can fail over.
- **Whether a waiting ask ever fails by itself**: the silent host was measured for 45 seconds,
  and nothing failed until the input closed; a longer silence was not.
- **A built-in rule that denies outright**: none was met — every protected path the probe tried
  was asked of the host. Paths that are the person's own (their Claude Code settings, their home)
  were not tried, by design.
- **What `total_cost_usd` stands for under a subscription's rates**: the prices slice's.
