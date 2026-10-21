# Partner

An assistant you can talk to on your PC: say a sentence, it queues the task, runs it, and answers out loud.

[简体中文](README.md) | [繁體中文](README.zh-TW.md) | **English**

Partner does not replace coding agents or work agents. It adds a voice entry point to the workflow you already have: trigger tasks by voice, keep configuration to a minimum, and download local voice engines and model services separately, only when you need them. It is for people who want to "just say it" on a Windows desktop without wiring up a pile of services.

## What it is / what it is not

| It is | It is not |
|---|---|
| A Windows desktop app where voice and text share one input queue | A coding agent or IDE plugin, or a replacement for the work agent you already use |
| A client for your own OpenAI-compatible model endpoint | A bundled LLM or cloud service |
| Optional local voice engines you download separately and can switch off at any time | An installer that makes you download 3 GB of models first |

## Key points

- **One session, viewed by date**: there is a single conversation, nothing to create, switch or manage; memory and context stay continuous. The UI renders it by day, with a year → month → day picker over the days that have conversation, opening on the newest day.
- **Voice control**: a wake word opens the gate; speech is segmented, transcribed and queued automatically. Replies are read sentence by sentence, and the microphone pauses during playback.
- **One-stop, minimal setup**: fill in a model endpoint on one settings page and it runs. The desktop shell picks the port, starts the API and injects the token; model names and audio devices come from drop-downs instead of typing; changes apply without a restart.
- **Split download for local engines**: the installer carries only two small server scripts. TTS (about 3.0 GB) and wake word + speech recognition (about 380 MB) are downloaded separately under "Settings → Local engines", with resume and verification. Switching an engine off ends its process and gives back all VRAM and RAM.

## One session + rendering by date

Every input (typing, voice, scheduled tasks, sub-agent reports) goes into the same session log and runs in arrival order.

- **A day is only a view**: the log is not split by day; picking a day only chooses which part is shown. A message sent while viewing an old day still lands at the end of the log.
- **Only days with conversation are listed**: empty days cannot be picked; a day appears as soon as it gets its first message.
- **Your calendar decides the day**: the frontend sends its UTC offset, so "which day" follows your time zone.
- **No jump at midnight**: while a turn that started before midnight is still writing, the view stays on that day and switches once idle.

## Features

| Feature | What it solves | Docs |
|---|---|---|
| Memory | Remembers facts and preferences you mentioned and brings them back later; you can view, re-tier and delete them | [memory](doc/memory.en.md) |
| Sub-agents | The main agent talks and delegates; desktop work and file reading run in the background on dedicated sub-agents | [sub-agents](doc/sub-agents.en.md) |
| Desktop control | Lets the model see windows, press controls, type and launch programs, with per-step approval | [desktop-control](doc/desktop-control.en.md) |
| Voice | Wake, recognition and read-aloud end to end, using local engines or external services | [voice](doc/voice.en.md) |
| Schedules | At the set time, a sentence enters the queue as if you had sent it; you or the assistant can create them | [schedules](doc/schedules.en.md) |
| MCP | Connects stdio MCP servers; their tools ask before every call by default | [mcp](doc/mcp.en.md) |
| Skills | Write a routine as a `SKILL.md`, upload or create it, and the skill list goes into the model prompt | [skills](doc/skills.en.md) |

## Quick start

### Prerequisites

| Item | Requirement |
|---|---|
| OS | Windows 10 2004 or later |
| .NET | .NET 8 SDK |
| Node | Node 22 + npm (not pnpm: esbuild fails to install) |
| Rust | cargo 1.98+ (only for the desktop shell) |
| WebView2 | Included with Win10+ |
| Model | An OpenAI-compatible endpoint, such as a local Ollama |

### Install and run

```bash
npm --prefix ui install          # first time
npx --prefix ui tauri dev        # starts the frontend + desktop shell; the shell starts the API
```

Then open "Settings → Chat model", enter the endpoint and pick a model. Without a model the queue is not executed, and the UI says what is missing. Environment variables also work:

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...   # optional
```

### Build the installer

```bash
bash scripts/build-shell.sh      # publish API → build frontend → compile shell → NSIS installer
```

### Run the checks only

```bash
bash scripts/ci.sh               # build, unit tests, smoke, capability gate, frontend, shell
SKIP_SHELL=1 bash scripts/ci.sh  # skip the slow shell build
```

## Documentation

See [doc/README.en.md](doc/README.en.md) for the full index, including development notes and configuration for each feature.

## Project layout

| Path | Contents |
|---|---|
| `src/Partner.Core` | Session, queue, agent turns, memory, scheduler, MCP, Skills, governance |
| `src/Partner.Desktop` | Window enumeration, capture, input injection, action verification |
| `src/Partner.Voice` | Microphone input, segmentation, ASR/TTS clients, playback |
| `src/Partner.Api` | Local HTTP API, queue consumer, voice host, local engine management |
| `ui/` | Vue 3 frontend (Simplified Chinese / Traditional Chinese / English UI) |
| `src-tauri/` | Tauri 2 desktop shell |
| `engines/` | Server scripts for the local voice engines; runtimes and models stay out of git |
| `scripts/` | CI, packaging and engine install scripts |

## FAQ

**Do I have to download the local voice engines?** No. Text input does not need voice, and voice can use ASR/TTS services you already run.

**Where is my data?** By default in `%LocalAppData%/Partner/workspace`: session log, queue, memory database and settings. The memory database is the only copy; deleting it loses it.

**macOS / Linux?** Not supported. Desktop control and audio depend on Win32 APIs.

## License

To be added (the repository has no LICENSE file yet).
