# Memory Services

[简体中文](./services.md) | [繁體中文](./services.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

"Memory Services" connect external agents and plugins to Partner: they let your Partner serve any LLM-compatible platform, such as game AI mods, editor/browser extensions, or other agent frameworks. The app can host multiple services, each on its own `127.0.0.1` port, proxying an upstream OpenAI or Anthropic model. Each request gets main-memory recall injected, an optional custom prompt prepended, and the interactions are periodically summarized into one sentence written back to the main memory store.

## What it is for

Game mods, extensions and the like usually connect to their own model: they don't know who you are and don't remember the last conversation. Point their model URL at a memory service and they gain access to Partner's memory; what happens on their side is also written back to the main memory, so Partner knows about it when you ask in the main chat.

Prerequisites:
- The program supports a custom Base URL, API key and model name (most mods that advertise OpenAI/Anthropic compatibility do).
- Runs on the same machine as Partner (services listen on `127.0.0.1` only).
- Protocol match (pick `openai` for an OpenAI-compatible upstream, `anthropic` for Anthropic).

Steps:
1. Add a service on the Memory Services page and copy its token.
2. In the mod/extension, set:
   - **Base URL**: `http://127.0.0.1:<port>/v1`
   - **API Key**: the service token
   - **Model**: the configured model name

## Core mechanics

- **Reverse proxy**: swaps auth and maps the model name; everything else (tools, stream, response format, etc.) is forwarded verbatim.
- **Stateless**: the service keeps no session; the client maintains its own history.
- **Memory augmentation**: each request retrieves main memory and injects it before the user message; an optional custom prompt can be prepended.
- **Auto summary**: interactions in the current window are periodically summarized into one sentence and written to main memory, tagged with source `service:<id>`.
- **Isolation**: listens on `127.0.0.1` only and rejects requests whose model name doesn't match the configuration.

Recall targets only the last user message that contains text; cross-service recall is not supported. Summary tasks are written asynchronously after the interaction ends.

## Managing services in the UI

The **Memory Services** page in the sidebar:
- Create a service and record the plaintext token (shown only once).
- Live monitoring: run state, request count, rounds pending summary, and the most recent memories written.
- Card actions: summarize now, rotate token, delete (optionally also purge the memories it wrote).
- Hot reload of configuration: changing port or protocol requires a restart of the service.

## Key fields

| Field | Description |
|---|---|
| Name | Source tag shown in summaries |
| Protocol | Must match the upstream protocol exactly; no protocol conversion |
| Context length | Inherits the main model by default |
| Recall / summary | Toggleable; configure the token budget and summary interval |
| Upstream | Custom baseUrl, model name, API key, etc. |

## Summary and timeout control

- Requests have a timeout before the response headers arrive (inherited from the main model or custom); streaming responses are unlimited. Enable `stream: true` for long streaming replies.
- Rate-limit and retry headers from the upstream (e.g. `retry-after`, `x-ratelimit-*`) are forwarded verbatim to the SDK so clients can retry.

## Memory deletion and security

> **Service memories are written into the main store; deleting a conversation does not cascade-delete them.**

Tick "Also delete the memories it wrote" when deleting the service on the Services page to purge them.
*Security note*: because external content gets summarized into main memory, there is an indirect prompt-injection risk — only allow trusted clients to reach the service port and review summaries periodically. Turning off the "Summary" toggle stops new writes; existing memories must be deleted manually.
