# Z.AI fixtures

The answers the glm client's tests replay: each file one streamed answer of `POST /api/paas/v4/chat/completions` - its
chunks as server-sent events, `data: [DONE]` last - or one refusal's body. The tests play them through an
`HttpMessageHandler` that stands in for the network (`ApiEndpoint`), cut into chunks of a few bytes.

Z.AI's reference leaves the streamed chunk out, so these are what slice 25's probe (`ZaiApiProbeTests`) met live on
2026-10-26, on `glm-5.3` - but for the ones a request cannot ask for (marked written), which are written in the probe's
shape from the reference's finish reasons and error codes. They are curated the way the Claude Code recordings are
(`../claude-code/README.md`): each chunk's `id` is its date and 22 counting digits, each call's `call_` and 24 counting
digits, in order of first appearance; nothing of an account and no key is ever kept; token counts stay as they came.

| File | What it is |
|---|---|
| `answer.sse` | live: reasoning, then text; `stop` |
| `tool-use.sse` | live: a call of `view_file` whose arguments stream in pieces, no reasoning; `tool_calls` |
| `two-calls.sse` | live: two calls in one turn, by their indexes, one after the other; `tool_calls` |
| `reasoned.sse` | live, at a high effort with the reasoning kept across turns: reasoning, then text, after a call's answer; `stop` |
| `cut-off.sse` | live: text cut off at its length; `length` |
| `cut-off-thinking.sse` | live: an answer cut off while it reasoned, before it wrote anything; `length` |
| `cut-off-call.sse` | live: a call cut off part way through its arguments; `length` |
| `cached.sse` | live: a long prefix asked again, the cache serving most of the prompt; `stop` |
| `key-refused.json` | live: the 401's body, code `1000` |
| `model-unknown.json` | live: the 400's body for a model Z.AI does not know, code `1211` |
| `sensitive.sse` | written: an answer Z.AI ended as sensitive; `sensitive` |
| `network-error.sse` | written: an answer Z.AI ended for a network error; `network_error` |
| `context-full.sse` | written: an answer the model's context could hold no more of; `model_context_window_exceeded` |
| `stream-error.sse` | written: an error chunk part way, code `1234` |
