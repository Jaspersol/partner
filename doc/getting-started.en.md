# Getting started

[简体中文](./getting-started.md) | [繁體中文](./getting-started.zh-TW.md) | **English**

Return to the [documentation index](./README.en.md).

## 1. Prepare the environment

| Item | Requirement |
|---|---|
| OS | Windows 10 2004 or later |
| .NET | .NET 10 SDK |
| Node | Node 22 + npm. Do not use pnpm: it blocks dependency build scripts by default, which prevents esbuild from installing |
| Rust | cargo 1.98+, needed only for the desktop shell |
| WebView2 | Included with Win10 and later |
| Hardware | Voice requires a microphone and speakers; local TTS requires a GPU |

## 2. Start the app

```bash
npm --prefix ui install          # first run
npx --prefix ui tauri dev        # development mode; Vue changes take effect immediately
```

The desktop shell selects an unused port, starts the API and injects the access token into the UI. No manual configuration is required for this step.

Launching it again focuses the existing window instead of starting another API. When the local service cannot start or exits repeatedly, the connection page shows the reason, exit code, failure count and log path; inspect the log and then choose Retry. The default log is `%LOCALAPPDATA%\Partner\partner-api.log`.

## 3. Configure a model

Open “Settings → Chat model”:

1. Select the protocol: choose OpenAI-compatible for most local services, and Anthropic for Claude or an endpoint implementing Anthropic Messages.
2. Enter the service URL: for example `http://127.0.0.1:11434/v1` for OpenAI-compatible services or `https://api.anthropic.com/v1` for Anthropic.
3. Select a model from the drop-down. The list comes from the endpoint, and failures include their reason.
4. Enter an API key when required. It is write-only; after saving, the UI only indicates that it has been set.

Saved changes apply immediately. Without a model, messages remain queued and the input area says that no model is configured. Anthropic currently does not request extended thinking; the “thinking strength” control applies only to OpenAI-compatible endpoints.

The editor is temporarily locked while settings are being saved or discarded, then becomes editable again. An API key is never echoed after it has been saved.

Environment variables are also supported, with the values from Settings taking precedence:

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...       # optional
export PARTNER_LLM_PROTOCOL=openai   # optional: openai / anthropic
```

## 4. Enable voice (optional)

Text input works without voice. To use voice, see [Voice](./voice.en.md): download the local engines or provide the URLs of ASR/TTS services you already run.

## 5. Data locations

The default workspace is `%LocalAppData%/Partner/workspace`:

```
<workspace>/
  session/events.jsonl    session log
  queue/items.jsonl       input queue
  memory/memory.db        memory database (SQLite)
  memory/backups/         database backups (taken before daily maintenance, newest 7 kept)
  scheduler/              scheduled tasks
  skills/                 skills
  media/                  attachments
```

The session and queue can both be rebuilt from their logs. The memory database is backed up to `memory/backups/` at least once a day: startup catches up when necessary, and daily maintenance checks again. The newest 7 copies are retained; writes made after a backup are not included in it.

During session-history deletion, a failed log replacement is reported. Writing can continue if the original file can be reopened. If it cannot, the store enters a faulted state and rejects later writes; resolve file-lock or permission problems before restarting the app.

## 6. Build the installer

```bash
bash scripts/build-shell.sh      # SKIP_INSTALLER=1 compiles the shell only
```

The script runs in this order: publish the API, build the frontend, compile the shell, then create the NSIS package. Changing that order can leave the installer with an old API.

For the normal installer, Tauri’s `beforeBuildCommand` builds the frontend once. With `SKIP_INSTALLER=1`, the script builds the frontend once itself before compiling the shell. Calling `tauri build` directly still builds the frontend, but the API must have been published first.
