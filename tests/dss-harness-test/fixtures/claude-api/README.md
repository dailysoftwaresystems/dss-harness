# Claude API fixtures

The answers the claude-api client's tests replay: each file is one streamed answer of `POST /v1/messages`, as
server-sent events, under `anthropic-version: 2026-10-21`. The tests play them through an `HttpMessageHandler` that
stands in for the network (`ApiEndpoint`), cut into chunks of a few bytes - a character split across two of them - so
what the client reads is what it would read off a connection.

These are written from the API's reference, not recorded: a declined request, a context too full and a cut-off cannot
be asked for, and slice 24b's live smoke is the first run against the API. Each answer a live run meets that differs
from these is kept beside them, curated the way the Claude Code recordings are (`../claude-code/README.md`): a
server id is its prefix (`msg_`, `toolu_`, `srvtoolu_`, `req_`, `fct_`) and 24 counting digits, in order of first
appearance; nothing of an account and no key is ever kept.

| File | What it is |
|---|---|
| `answer.sse` | a thinking block with its signature, then text (a character beyond ASCII among it); `end_turn` |
| `tool-use.sse` | thinking, text and a call of `view_file` whose input streams in pieces; `tool_use` |
| `cut-off.sse` | text cut off at its length; `max_tokens` |
| `cut-off-call.sse` | a call cut off part way through its input; `max_tokens` |
| `pause-turn.sse` | text the API paused; `pause_turn` |
| `refusal.sse` | a request declined before any output, its category and a credit token; `refusal` |
| `refusal-part-way.sse` | a request declined after some text, no category and no token; `refusal` |
| `context-full.sse` | an answer the model's context could hold no more of; `model_context_window_exceeded` |
| `context-edits.sse` | an answer after the server cleared old tool uses, said in `context_management.applied_edits` |
| `redacted-thinking.sse` | a redacted thinking block, then text |
| `stream-error.sse` | an `error` event part way, the API overloaded |
| `server-tool.sse` | a block of a tool of the API's own, which no request declares |
| `unknown-delta.sse` | a delta of a type the client does not put back together |
