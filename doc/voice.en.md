# Voice

[简体中文](./voice.md) | [繁體中文](./voice.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

Say a sentence; it enters the queue, runs a turn, and the reply is read out. Voice and typing share one queue and run in arrival order.

## Pipeline

```
microphone → pause segmentation → ASR → queue → turn → session
                                                         ↓
speakers ← ordered playback ← TTS ← sentence split ← new assistant reply
```

- Input and output start and fail independently. A microphone that will not open does not stop read-aloud.
- Only replies that appear after start-up are read; history is not read out on launch.
- "Mute the microphone while speaking" is recommended, so the assistant does not hear itself as you.
- "Interrupt the assistant by speaking": speak for about half a second while it talks and it stops. There is no echo cancellation, so use it with headphones.

## Two ways to connect

| Option | Good for | Where |
|---|---|---|
| Local engines | Not wanting to deploy services yourself | Settings → Local engines, click "Download" |
| External services | ASR/TTS services you already run | Settings → Voice input / Voice output, enter the service URL |

## Local engines: split download

The installer carries only two small server scripts. Runtimes and models are not bundled; each engine downloads on its own:

| Engine | Purpose | Uses | Size | Default port |
|---|---|---|---|---|
| TTS (Qwen3-TTS 1.7B GGUF) | Read-aloud | About 1.9 GB VRAM | About 3.0 GB | 45102 |
| KWS + ASR (sherpa-onnx + SenseVoice) | Wake word + recognition | RAM, CPU only | About 380 MB | 45103 |

- If you only need recognition, download only KWS + ASR. No GPU required.
- Downloads resume; SHA-256 is verified when available; when the server reports a length it is checked, and a truncated file does not count as success.
- A dropped connection is retried three times from the bytes already on disk, and 60 seconds without data counts as dropped, so the download button does not have to be pressed again.
- "Check" on the settings page looks at the files on disk and lists what is missing. You can also point it at an existing engine directory.
- Default paths are stored relative to the app directory, so moving or reinstalling the app needs no settings change.
- Switching an engine off ends its process (the whole tree) and returns all VRAM and RAM.

Download URLs can be overridden with `PARTNER_ENGINE_TTS_URL`, `PARTNER_ENGINE_KWS_URL` (and the matching `_SHA256`).
One engine may span several archives: a GitHub release asset may not reach 2 GiB and TTS does not fit in one, so the app fetches them in order and unpacks each into the same folder.
`scripts/pack-engines.ps1` builds those archives from the local `engines/` (the models are not in git): it rebuilds a relocatable runtime, splits the packs, hashes them, and prints the URL table for `EnginePackage.cs`.
Every pack carries a `NOTICE/` folder with upstream provenance, a statement of changes and the full licence texts; those licences are separate from MIT, see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

On a dev machine, a script installs them into the repository's `engines/`, where the settings page finds them:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1              # both
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 -Engine kws  # recognition only
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 -Engine tts  # read-aloud only
```

Cloning a voice from wav/mp3 needs `ffmpeg` on PATH; the bundled voice anchors do not.

## Wake word

With the KWS engine on, voice input waits for the wake word: the microphone keeps listening, but nothing is transcribed until the word is heard.

- Set wake words under "Settings → Voice input → Wake word", comma-separated. Leave it empty to keep the engine's current words.
- "Wait for you after waking" defaults to 5000 ms, time to think about what to say. Once you start talking, "Pause that ends a phrase" takes over.
- "Chime on wake" plays a sound on detection; voice output must be on.
- Words right after the wake word are not cut off.

## What a settings change restarts

Changing input, output, ASR or TTS restarts the voice host (devices reopen). Changing only the model does not, so a recognition in progress is not interrupted.

## Known limits

- Automated tests of the full loop use a synthetic source; a whole sentence has not been run end to end on a real microphone.
- The official KWS package only ships chunk-16, so wake latency is about 320 ms.
