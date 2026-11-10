# Memory Services

[简体中文](./services.md) | [繁體中文](./services.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

Memory Services connect external agents and plugins: they let your own Partner join any platform that can use a large model, such as game AI mods, editor or browser plugins, and other agent frameworks. The app can host several services. Each gets its own `127.0.0.1` port, proxies an upstream OpenAI or Anthropic model, injects main-memory recall into requests, optionally prepends a custom prompt, and periodically summarizes the interactions into one sentence written back into the main memory store.

## What it is for

Game mods, plugins and agent frameworks usually talk to a model of their own that does not know who you are or what you talked about last time. Point their model address at a Memory Service and they use Partner's memory; what happens on their side is written back to the main memory too, so Partner knows about it when you bring it up in the main conversation.

A program can connect when:

- it lets you enter the model address (Base URL), API key and model name yourself. Most mods and plugins that advertise "OpenAI-compatible API" or "Claude API" support do;
- it runs on the same computer as Partner (services listen on `127.0.0.1` only);
- it speaks the same protocol as the service's upstream: choose `openai` for an OpenAI-compatible upstream, `anthropic` for an Anthropic one.

To connect, add a service on the Memory Services page, copy its token, and enter in the mod or plugin:

| Setting in the mod / plugin | Value |
|---|---|
| Base URL / endpoint | `http://127.0.0.1:<port>/v1` |
| API key | the service's token |
| Model name | the service's "Model name" |

An `openai` service accepts `POST /v1/chat/completions`, an `anthropic` service `POST /v1/messages`. Some Anthropic clients, including the official SDK, append `/v1` themselves; for those, enter `http://127.0.0.1:<port>` without `/v1`.

The caller's tools (function calling), streaming and its own conversation history keep working; the service does not replace them, and it does not expose Partner's MCP tools or desktop control to the caller.

## What it is

- Each instance is a reverse proxy: it swaps the client's authentication for the upstream key and maps the exposed model name to the upstream one. Everything else passes through unchanged — `tools`, `tool_choice`, `stream`, `response_format`, and upstream `tool_calls`, SSE frames, `usage` and error bodies.
- There is **no session**: clients keep their own history (stateless) and send the full history on every request; the service only buffers pending rounds locally.
- Every request retrieves main memory and injects it in front of the last user message that contains text; a custom prompt can be prepended (empty = leave the request unchanged).
- On a timer (or on shutdown, deletion, or a manual "Flush now") the interactions in the window are summarized into one sentence written to the main memory store, tagged with origin `service:<id>`.
- It listens on `127.0.0.1` only, never on the LAN. The `model` in a request must equal the configured exposed name, or the service returns 404.

## Creating a service in the UI

The **Memory Services** page in the sidebar:

1. Click "Add" and fill in the fields below, then save. Saving starts the instance on `http://127.0.0.1:<port>/v1`.
2. The server generates an ID and a token on creation; the **plaintext token is shown only once** (on creation or rotation), so copy and keep it.
3. Each card shows running state and errors, request count, pending rounds, last flush time, and the most recent memory written, with a link to the Memory page.
4. Card actions: Flush now, Rotate token (plaintext shown once), Delete (with an optional "Also delete the memories it wrote" checkbox).
5. Changing the port or protocol rebuilds the listener; other fields apply hot on the next request. Each port has its own token.

## Fields

| Field | Description |
|---|---|
| Name | Display name, also used as the source label of summaries |
| Enabled | Turn off to stop the instance |
| Port | 1024–65535; unique per instance and not the main API port |
| Protocol | `openai` or `anthropic`. **Must match the upstream's protocol**: pass-through never converts formats, so a mismatch is refused on save (an existing config answers 502) |
| Model name | The name exposed to clients; a request's `model` must equal it |
| Context length | Used to size the token budget for recall injection; inherits the main model by default |
| Custom prompt | Prepended when set; empty = leave the request unchanged |
| Recall | On/off, result limit, token budget; off = no memory injected |
| Summary | On/off, interval (minutes), max wait (minutes); off = nothing written back |
| Upstream | "Inherit main model" by default, or a custom baseUrl, model, API key, max output tokens and timeout |
| Token | Per port; plaintext shown only once on creation/rotation |

## Management API

The management endpoints live on the main API host and use the startup Bearer token:

- `GET /api/services`, `GET /api/services/{id}`: list instances and their state.
- `POST /api/services`: create; the response returns the plaintext token **only this once**.
- `PUT /api/services/{id}`: update (leave token / upstream key empty to keep the current value).
- `DELETE /api/services/{id}?purgeMemories=false`: delete; `true` also soft-deletes the memories this service wrote.
- `POST /api/services/{id}/flush`: summarize immediately, returns the written memory ID.
- `POST /api/services/{id}/token`: rotate the token, returned once in plaintext.

## Calling the service

`model` must equal the exposed model name configured for the service. Both auth headers work with both protocols: `Authorization: Bearer <token>` or `x-api-key: <token>`.

OpenAI protocol (`POST /v1/chat/completions`, Bearer):

```bash
curl http://127.0.0.1:<port>/v1/chat/completions -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"model":"<model>","messages":[{"role":"user","content":"..."}]}'
```

Anthropic protocol (`POST /v1/messages`, `x-api-key`):

```bash
curl http://127.0.0.1:<port>/v1/messages -H "x-api-key: <token>" -H "anthropic-version: 2023-06-01" -H "Content-Type: application/json" -d '{"model":"<model>","max_tokens":1024,"messages":[{"role":"user","content":"..."}]}'
```

List models (`GET /v1/models`):

```bash
curl http://127.0.0.1:<port>/v1/models -H "Authorization: Bearer <token>"
```

Streaming (`stream: true`), tool calls and `response_format` pass through unchanged; SSE is forwarded frame by frame.

### Timeouts and forwarded response headers

- Waiting for the upstream response headers is bounded (the main model's timeout when inheriting, configurable for a custom upstream); past it the call answers `504` with a protocol-shaped error body. Once the headers arrive, the streamed transfer itself has no deadline. A non-streaming request gets its headers only when the whole reply is done, so a long answer from a reasoning model can hit the limit; use `stream: true` for those.
- Upstream `retry-after`, `x-ratelimit-*`, `anthropic-ratelimit-*`, `request-id`, `x-request-id`, `openai-processing-ms` and `cache-control` are forwarded verbatim so an SDK can back off correctly; `set-cookie` and the framing headers (`content-length`, `transfer-encoding`, `connection`) are not forwarded.

## Where the memories live

> **Service memories are written into the MAIN memory store and CAN be recalled by the main session; deleting a conversation (or withdrawing a message) does NOT delete service memories — delete them on the Services page.** The service writes its one-sentence summaries into the main memory store (origin `service:<id>`), so later turns of the main session recall them like any other memory. Deleting or withdrawing a conversation in the main session does **not** cascade to these memories.

To remove them, delete the service on the Services page and tick "Also delete the memories it wrote" (equivalent to `DELETE /api/services/{id}?purgeMemories=true`); leave it unticked to keep them. Deleting a service stops the listener first (the stop flushes the final window), then purges — so no request can slip in afterwards and write a fresh memory.

## Security notes

- External content is summarized into the main memory, and the main session later recalls those summaries, so there is an **indirect prompt-injection** risk: an untrusted caller could influence the main agent through the summary text. Only let trusted clients reach the port, and review the summaries that get written.
- To turn summary off: switch the "Summary" toggle off on the Services page (i.e. `Summary.Enabled=false`). Recall injection still works, but nothing is written back to the main memory store; delete already-written memories as described above.
- On shutdown every instance runs its final summary in parallel. An instance whose model call does not finish within the budget is written with the template instead, so quitting never loses buffered rounds.
- A token cannot be empty: saving generates one for a new instance, validation requires at least 32 characters, and an instance with no token rejects every request.
- Each instance listens on `127.0.0.1` only and is never exposed to the LAN. Port requests still need that instance's token; the management endpoints use the main API's startup token.
