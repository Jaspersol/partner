# Getting started

[简体中文](./getting-started.md) | [繁體中文](./getting-started.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

## 1. Prepare the environment

| Item | Requirement |
|---|---|
| OS | Windows 10 2004 or later |
| .NET | .NET 8 SDK |
| Node | Node 22 + npm. Not pnpm: it blocks dependency build scripts by default, so esbuild fails to install |
| Rust | cargo 1.98+, only for the desktop shell |
| WebView2 | Included with Win10+ |
| Hardware | Voice needs a microphone and speakers; local TTS needs a GPU |

## 2. Start

```bash
npm --prefix ui install          # first time
npx --prefix ui tauri dev        # dev mode; Vue edits apply immediately
```

The desktop shell picks a free port, starts the API and injects the access token into the UI. Nothing to configure at this step.

## 3. Configure a model

Open "Settings → Chat model":

1. Pick the API format: OpenAI-compatible for most local services, Anthropic for Claude or any endpoint that implements the Anthropic Messages API.
2. Enter the endpoint: `http://127.0.0.1:11434/v1` for OpenAI-compatible, `https://api.anthropic.com/v1` for Anthropic.
3. Pick a model from the drop-down. The list is fetched from the endpoint; if that fails, the reason is shown.
4. Enter an API key if needed. It is write-only: after saving, the UI only shows that one is set.

Changes apply on save. Without a model, messages stay in the queue and the input area says the model is not configured. The Anthropic format does not request extended thinking; the thinking-strength setting applies to OpenAI-compatible endpoints only.

Environment variables also work; values from the settings page take precedence:

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...   # optional
export PARTNER_LLM_PROTOCOL=openai   # optional: openai / anthropic
```

## 4. Turn on voice (optional)

Text input does not depend on voice. For voice, see [Voice](./voice.en.md): download the local engines, or enter ASR/TTS services you already have.

## 5. Where data lives

The default workspace is `%LocalAppData%/Partner/workspace`:

```
<workspace>/
  session/events.jsonl    session log
  queue/items.jsonl       input queue
  memory/memory.db        memory database (SQLite)
  scheduler/              scheduled tasks
  skills/                 skills
  media/                  attachments
```

The session and queue can be rebuilt from their logs. The memory database is the only copy; delete it and it is gone.

## 6. Build the installer

```bash
bash scripts/build-shell.sh      # SKIP_INSTALLER=1 compiles the shell only
```

The script runs "publish API → build frontend → compile shell → NSIS package" in that order. The order matters: otherwise the installer ships an old API.
