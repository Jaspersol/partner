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
`scripts/pack-engines.ps1` builds those archives from the local `engines/` (the models are not in git): it rebuilds a relocatable runtime, splits the packs, hashes them, and prints the URL table for `EnginePackage.cs`. Engine tags are read-only: the script fails when the tag already carries an asset of the same name. Ship new engines under a new tag (`engines-v2`), otherwise the SHA-256 pins of every published installer break.
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

## Voice approval

With voice output on, a request that needs your approval is read aloud. With voice input on as well, it can be answered by voice.

- The prompt names the tool and the asker (a sub-agent's name is included); raw arguments are never read, so paths and clipboard contents do not reach the speaker.
- The prompt is inserted after the sentence currently playing and before assistant sentences that have not started. An approval lives for 120 seconds, so waiting behind a whole reply can outlive it.
- While the approval is pending and the speaker is idle, the microphone keeps transcribing without a wake word: say "批准执行" (approve) or "拒绝执行" (deny). The prompt does not have to finish first, and the request stays answerable for as long as it exists.
- With a wake word configured, the bypass applies only to those two commands. Other speech captured while waiting, without a wake word, is not queued. Without a wake word configured, ordinary speech still behaves as a message.
- While the prompt or an assistant reply is playing, or audio is still queued, commands are not accepted. Approval prompts and results force the microphone closed even when "Mute the microphone while speaking" is off.
- With several pending requests, one command answers only the oldest, matching the first banner on screen; the confirmation says how many remain.
- Voice and the banner use the same answer path; no answer still denies after 120 seconds. A desktop approval still grants 10 further steps, and dangerous actions still ask every time.
- No voice output means no voice approval: without a spoken prompt there is no question to answer.
- A TTS failure does not disable voice approval: with nothing playing, the command still works. System mute is not detectable, but the request is on screen.

## What a settings change restarts

Changing input, output, ASR or TTS restarts the voice host (devices reopen). Changing only the model does not, so a recognition in progress is not interrupted.

## Known limits

- Automated tests of the full loop use a synthetic source; a whole sentence has not been run end to end on a real microphone.
- The official KWS package only ships chunk-16, so wake latency is about 320 ms.
- Voice approval matches whole commands, but ASR can still mishear one (for example, dropping "不"); the prompt and the final decision are both read out.
