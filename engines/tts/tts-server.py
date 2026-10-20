"""engines/tts/tts-server.py — the TTS HTTP shell shipped with this repo.

Wraps HaujetZhao/Qwen3-TTS-GGUF's TTSEngine in OpenAI-compatible endpoints for Partner's
`tts.protocol=openai` setting:

    POST /v1/audio/speech     { "input": ..., "voice": ..., "reference_text": ...,
                                "response_format": "wav", "speed": 1 }
                              -> audio/wav (16-bit PCM at 24 kHz, returned in one response)

`reference_text` is the transcript of the audio clone source, meaning what is actually said in the reference
recording. When omitted it falls back to a `<wav>.txt` or `<stem>.txt` sidecar, and with neither it uses
zero-shot cloning from the speaker embedding alone. It is **not** the text to speak: passing `input` as the
transcript pairs the reference timbre with the wrong text and produces garbled speech.

Usage (from engines/tts after pip install -r requirements.txt):

    .venv/Scripts/python.exe tts-server.py --voice vivian --port 45102 --device vulkan

Models come from `--model-dir` (default `model/`, meaning engines/tts/model). `voice` is resolved in order:

  1. A path such as `output/elaborate/Vivian.json` -> used directly as a voice anchor (Base model cloning)
  2. A name such as `vivian` or `晓晓` -> matches `output/elaborate/<name>.json` (the zip ships 8 anchors)
  3. Any other name -> passed to upstream as a built-in speaker ID (vivian/serena/... need a CustomVoice
     export; this repo installs Base by default)

Each request may override the default voice with `voice`; without it the startup anchor is used.
"""
from __future__ import annotations

import argparse
import io
import json
import os
import struct
import sys
import threading
import time
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

# Upstream prints emoji, and the Windows console defaults to GBK, so `print('🔊 …')` raises
# UnicodeEncodeError. The environment variables also cover spawned children (which re-import this module);
# reconfigure covers the current process.
os.environ.setdefault("PYTHONIOENCODING", "utf-8")
os.environ.setdefault("PYTHONUTF8", "1")
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    except (AttributeError, ValueError):  # Redirected to a non-TextIO stream; not worth failing over.
        pass

ENGINE_DIR = Path(__file__).resolve().parent

# GUI zip layout: Qwen3-TTS-GUI.exe plus internal/ (package and llama.dll) plus output/elaborate/
# (voice anchors). The package lives under internal/, so internal/ must be on sys.path; upstream also
# writes logs under PROJECT_ROOT, which is internal/.
#
# Append rather than insert: internal/ is a PyInstaller Python 3.14 runtime and would shadow this venv's
# _ctypes if it came before the stdlib, causing "Module use of python314.dll conflicts with this version
# of Python". Appending keeps venv site-packages first and uses internal/ only for its own packages.
if str(ENGINE_DIR) not in sys.path:
    sys.path.insert(0, str(ENGINE_DIR))
if str(ENGINE_DIR / "internal") not in sys.path:
    sys.path.append(str(ENGINE_DIR / "internal"))

from qwen3_tts_gguf.inference import TTSEngine, TTSConfig  # noqa: E402
from qwen3_tts_gguf.inference.schema.constants import SPEAKER_MAP  # noqa: E402

SAMPLE_RATE = 24_000
VOICE_ANCHORS = ENGINE_DIR / "output" / "elaborate"

# Upstream streaming playback must stay off. TTSConfig.streaming defaults to True and DecoderProxy starts
# a SpeakerWorker unconditionally (a bare sd.OutputStream on the system default device); with streaming on,
# every decoded chunk also reaches play_q (proxy.py:126), so the engine plays it itself. That adds a second
# audible copy regardless of the configured output device. This pipeline consumes the full WAV from HTTP,
# so streaming playback is useless here; turning it off removes the extra output and the chunked decode.
OFFLINE = TTSConfig(streaming=False)


def resolve_voice(voice: str | None) -> str | None:
    """Name -> a voice source the engine accepts. Returns None when unrecognized; the caller must report an
    error rather than silently use the default voice.

    Three matches: an anchor path (json/wav) is used as is; an anchor name (case-insensitive) resolves to a
    json under output/elaborate; a built-in speaker ID (vivian/serena/...) is passed through to spk_embd.

    The last path is upstream's fallback: when the name is not in the table, map_speaker only warns and
    returns None, then synthesis still produces default-voice audio. Unrecognized names must therefore be
    rejected here, or a single typo in a voice name yields the default voice instead of an error.
    """
    if not voice:
        return None
    path = Path(voice)
    if path.suffix.lower() in (".json", ".wav") or path.exists():
        return str(path)
    for candidate in sorted(VOICE_ANCHORS.glob("*.json")):
        if candidate.stem.lower() == voice.lower():
            return str(candidate)
    if voice.lower() in SPEAKER_MAP:
        return voice
    return None


def read_reference_text(voice: str) -> str:
    """Transcript sidecar next to an audio anchor: `<path>.txt` first, then `<stem>.txt`. Empty if absent."""
    path = Path(voice)
    if path.suffix.lower() == ".json":
        return ""
    for candidate in (Path(str(path) + ".txt"), path.with_suffix(".txt")):
        try:
            if candidate.is_file():
                return candidate.read_text(encoding="utf-8").strip()
        except OSError:
            pass
    return ""


class EngineState:
    """One voice anchor per stream; concurrent requests are serialized because inference is single-stream."""

    def __init__(self, model_dir: str, default_voice: str | None, default_reference_text: str, device: str):
        onnx_provider = {"vulkan": "DML", "cpu": "CPUExecutionProvider"}.get(device, "DML")
        # Absolute path: a spawned decoder subprocess does not share this process's working directory.
        model_path = str(Path(model_dir).resolve())
        self.engine = TTSEngine(
            model_dir=model_path,
            onnx_provider=onnx_provider,
            llm_use_gpu=device != "cpu",
            verbose=False,
        )
        if not self.engine.ready:
            raise SystemExit(
                f"engine init failed — 检查 {Path(model_path)} 下六个模型文件是否齐全，"
                "以及 internal/log/latest.log（解码器子进程崩在这里，不看日志只会看到这句）"
            )

        self.lock = threading.Lock()
        self.stream = self.engine.create_stream()
        if default_voice:
            resolved = resolve_voice(default_voice)
            if resolved is None:
                raise SystemExit(
                    f"unknown voice {default_voice!r} — 锚点见 {VOICE_ANCHORS}/*.json，"
                    f"或内置 ID {sorted(SPEAKER_MAP)}"
                )
            default_voice = resolved
        self.default_voice = default_voice
        self.current_voice: str | None = None
        self.current_ref_text = ""

        if default_voice:
            if not self.apply_voice(default_voice, default_reference_text):
                raise SystemExit(f"voice anchor failed for {default_voice}")
        print(
            f"tts-server ready: model={model_dir} voice={default_voice!r} device={device} "
            f"playback={'off' if not OFFLINE.streaming else 'ON'}"
        )

    def apply_voice(self, voice: str | None, reference_text: str = "") -> bool:
        """Switch the voice anchor. The same (voice, transcript) pair is not re-extracted: rebuilding the
        anchor per sentence is slow and lets the timbre drift.

        An audio source needs its own transcript because text and codes align position by position in the ICL
        prompt; a wrong transcript garbles synthesis. Without a transcript this falls back to the sidecar, and
        without that to zero-shot cloning from spk_emb.
        """
        if not voice:
            return True

        text = (reference_text or "").strip() or read_reference_text(voice)
        if voice == self.current_voice and text == self.current_ref_text:
            return True

        path = Path(voice)
        if path.suffix.lower() == ".json" or voice.lower() in SPEAKER_MAP:
            # A JSON anchor carries its own transcript, so the passed text is ignored; built-in IDs use a
            # fixed sample sentence.
            ok = bool(self.stream.set_voice(voice, "你好，很高兴见到你。", config=OFFLINE))
        else:
            ok = bool(self.stream.set_voice(voice, text=text, config=OFFLINE))

        if ok:
            self.current_voice, self.current_ref_text = voice, text
        return ok

    @property
    def zero_shot(self) -> bool:
        """An audio anchor without a transcript is zero-shot only: with just spk_emb in the prompt, codec
        codes misalign against the text."""
        voice = self.current_voice
        if not voice:
            return True
        path = Path(voice)
        return (
            path.suffix.lower() != ".json"
            and voice.lower() not in SPEAKER_MAP
            and not self.current_ref_text
        )


def float32_to_wav_bytes(samples, rate: int) -> bytes:
    """float32 [-1,1] -> 16-bit mono WAV (RIFF header built with the stdlib, no extra dependency)."""
    import numpy as np

    pcm16 = np.clip(samples, -1.0, 1.0)
    pcm16 = (pcm16 * 32767.0).astype("<i2").tobytes()
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as writer:
        writer.setnchannels(1)
        writer.setsampwidth(2)
        writer.setframerate(rate)
        writer.writeframes(pcm16)
    return buffer.getvalue()


class Handler(BaseHTTPRequestHandler):
    state: EngineState  # set by serve()

    def log_message(self, fmt, *args):  # quieter: the runtime log inherits ours
        sys.stderr.write("tts-server: " + (fmt % args) + "\n")

    def do_GET(self):
        if self.path in ("/health", "/healthz"):
            self._json(200, {"ok": True})
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self):
        if self.path not in ("/v1/audio/speech", "/audio/speech"):
            self._json(404, {"error": "not found"})
            return

        length = int(self.headers.get("Content-Length") or 0)
        try:
            request = json.loads(self.rfile.read(length) or b"{}")
        except json.JSONDecodeError:
            self._json(400, {"error": "bad json"})
            return

        text = request.get("input") or ""
        if not text:
            self._json(400, {"error": "input is required"})
            return

        requested = request.get("voice")
        if requested:
            voice = resolve_voice(requested)
            if voice is None:
                self._json(400, {"error": f"unknown voice {requested!r}"})
                return
        else:
            voice = self.state.default_voice
        language = request.get("language") or "chinese"
        speed = float(request.get("speed") or 1.0)
        reference_text = (request.get("reference_text") or "").strip()

        with self.state.lock:
            try:
                if voice and not self.state.apply_voice(voice, reference_text):
                    self._json(400, {"error": f"voice anchor failed for {voice!r}"})
                    return

                result = self.state.stream.clone(
                    text, language=language, config=OFFLINE, zero_shot=self.state.zero_shot)
                if result is None or result.audio is None:
                    self._json(500, {"error": "synthesis failed"})
                    return

                samples = result.audio
                # Approximate speed with linear resampling (the engine has no speed parameter); only for
                # requests away from 1.0, at a small quality cost on short audio.
                if abs(speed - 1.0) > 0.01:
                    samples = _resample(samples, speed)

                wav = float32_to_wav_bytes(samples, SAMPLE_RATE)
            except Exception as error:  # noqa: BLE001
                self._json(500, {"error": f"synthesis failed: {error}"})
                return

        self.send_response(200)
        self.send_header("Content-Type", "audio/wav")
        self.send_header("Content-Length", str(len(wav)))
        self.end_headers()
        self.wfile.write(wav)

    def _json(self, code: int, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def _resample(samples, speed: float):
    """Linear-interpolation speed change. speed=2 halves the duration, and pitch changes with it: linear
    resampling alters both."""
    import numpy as np

    count = int(len(samples) / speed)
    if count <= 0 or len(samples) < 2:
        return samples
    source = np.linspace(0, len(samples) - 1, count)
    low = source.astype("i8")
    high = np.minimum(low + 1, len(samples) - 1)
    weight = (source - low)[None, :]
    return samples[low] * (1 - weight) + samples[high] * weight


def warm_up(state: "EngineState") -> None:
    """Synthesize one short line and discard it. The first line pays for anchor pre-decode and backend warm-up
    (about 0.69 s measured, 0.42 s steady state), so pay it at startup instead of on the user's first
    sentence. Failure is not fatal; it only means a slower first line."""
    started = time.monotonic()
    try:
        with state.lock:
            state.stream.clone("好的。", language="chinese", config=OFFLINE, zero_shot=state.zero_shot)
        print(f"tts-server warm-up done in {time.monotonic() - started:.2f}s")
    except Exception as error:  # noqa: BLE001
        print(f"tts-server warm-up skipped: {error}", file=sys.stderr)


def serve():
    parser = argparse.ArgumentParser(description="Qwen3-TTS GGUF OpenAI-compatible server")
    parser.add_argument("--model-dir", default=str(ENGINE_DIR / "model"), help="默认 engines/tts/model")
    parser.add_argument("--voice", default="Vivian", help="锚点名/路径/内置 ID；默认 Vivian（zip 自带锚点）")
    parser.add_argument("--reference-text", default="",
                        help="音频锚点的转写；为空时读 <wav>.txt，再没有走零样本")
    parser.add_argument("--port", type=int, default=45102)
    parser.add_argument("--device", default="vulkan", choices=["vulkan", "cpu"])
    args = parser.parse_args()

    Handler.state = EngineState(args.model_dir, args.voice, args.reference_text, args.device)
    warm_up(Handler.state)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"tts-server listening on http://127.0.0.1:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    serve()
