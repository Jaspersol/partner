"""engines/tts/tts-server.py — 本仓提供的 TTS HTTP 壳。

把 HaujetZhao/Qwen3-TTS-GGUF 的 TTSEngine 包成 OpenAI 兼容端点，给 Partner
设置里的 `tts.protocol=openai` 直接用：

    POST /v1/audio/speech     { "input": …, "voice": …, "response_format": "wav", "speed": 1 }
                              → audio/wav（16-bit PCM @ 24 kHz，一次性返回）

用法（engines/tts 下，先 pip install -r requirements.txt）:

    python tts-server.py --model-dir model-base --voice vivian --port 45102 --device vulkan

`voice` 可以是内置音色 ID（vivian/serena/…，CustomVoice 模型）或 wav/json 参考文件路径
（Base 模型克隆）；每次请求可用 `voice` 覆盖默认音色，未提供则用启动时的默认锚点。
"""
from __future__ import annotations

import argparse
import io
import json
import struct
import sys
import threading
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ENGINE_DIR = Path(__file__).resolve().parent

# qwen3_tts_gguf 包在 engines/tts/qwen3_tts_gguf/（GUI zip 解包后）
if str(ENGINE_DIR) not in sys.path:
    sys.path.insert(0, str(ENGINE_DIR))

from qwen3_tts_gguf import TTSEngine, TTSConfig  # noqa: E402

SAMPLE_RATE = 24_000


class EngineState:
    """一个流一个音色锚点；并发请求排队串行化（引擎本质是单流推理）。"""

    def __init__(self, model_dir: str, default_voice: str, device: str):
        onnx_provider = {"vulkan": "DML", "cpu": "CPUExecutionProvider"}.get(device, "DML")
        self.engine = TTSEngine(
            model_dir=model_dir,
            onnx_provider=onnx_provider,
            llm_use_gpu=device != "cpu",
            verbose=False,
        )
        if not self.engine.ready:
            raise SystemExit("engine init failed — missing model files?")

        self.lock = threading.Lock()
        self.stream = self.engine.create_stream()
        self.default_voice = default_voice
        if default_voice:
            if not self.stream.set_voice(default_voice, "你好，很高兴见到你。"):
                raise SystemExit(f"voice anchor failed for {default_voice}")
        print(f"tts-server ready: model={model_dir} voice={default_voice!r} device={device}")


def float32_to_wav_bytes(samples, rate: int) -> bytes:
    """float32 [-1,1] → 16-bit mono WAV（RIFX 头标准库拼，不引依赖）。"""
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

        voice = request.get("voice") or self.state.default_voice
        language = request.get("language") or "chinese"
        speed = float(request.get("speed") or 1.0)

        with self.state.lock:
            try:
                if voice and voice != self.state.default_voice:
                    if not self.state.stream.set_voice(voice, text=text):
                        self._json(400, {"error": f"voice anchor failed for {voice!r}"})
                        return

                result = self.state.stream.clone(text, language=language)
                if result is None or result.audio is None:
                    self._json(500, {"error": "synthesis failed"})
                    return

                samples = result.audio
                # speed ≠1 时线性重采样近似（引擎端没有 speed 参数）；
                # 只在偏离 1 的请求里做，代价是短音频上采样质量略降。
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
    """线性插值变速。speed=2 → 音长减半音调不变…不，这里音调也变——线性重采样本来就是变速变调。"""
    import numpy as np

    count = int(len(samples) / speed)
    if count <= 0 or len(samples) < 2:
        return samples
    source = np.linspace(0, len(samples) - 1, count)
    low = source.astype("i8")
    high = np.minimum(low + 1, len(samples) - 1)
    weight = (source - low)[None, :]
    return samples[low] * (1 - weight) + samples[high] * weight


def serve():
    parser = argparse.ArgumentParser(description="Qwen3-TTS GGUF OpenAI-compatible server")
    parser.add_argument("--model-dir", required=True, help="e.g. model-base / model-custom")
    parser.add_argument("--voice", default=None, help="内置音色 ID 或 wav/json 参考路径；不设则每次请求必须给 voice")
    parser.add_argument("--port", type=int, default=45102)
    parser.add_argument("--device", default="vulkan", choices=["vulkan", "cpu"])
    args = parser.parse_args()

    Handler.state = EngineState(args.model_dir, args.voice, args.device)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"tts-server listening on http://127.0.0.1:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    serve()
