"""engines/tts/tts-server.py — 本仓提供的 TTS HTTP 壳。

把 HaujetZhao/Qwen3-TTS-GGUF 的 TTSEngine 包成 OpenAI 兼容端点，给 Partner
设置里的 `tts.protocol=openai` 直接用：

    POST /v1/audio/speech     { "input": …, "voice": …, "response_format": "wav", "speed": 1 }
                              → audio/wav（16-bit PCM @ 24 kHz，一次性返回）

用法（engines/tts 下，先 pip install -r requirements.txt）:

    .venv/Scripts/python.exe tts-server.py --voice vivian --port 45102 --device vulkan

模型走 `--model-dir`（默认 `model/`，即 engines/tts/model）。`voice` 三种写法，按顺序解析：

  1. `output/elaborate/Vivian.json` 这类路径 → 直接当音色锚点（Base 模型克隆）
  2. `vivian` / `晓晓` 这类名字 → 命中 `output/elaborate/<名字>.json`（zip 自带 8 个锚点）
  3. 其它名字 → 交给上游当内置说话人 ID（vivian/serena/… 需 CustomVoice 导出，本仓默认装的是 Base）

每次请求可用 `voice` 覆盖默认音色，未提供则用启动时的默认锚点。
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

# 上游到处 print emoji；Windows 控制台默认 GBK，`print('🔊 …')` 会抛 UnicodeEncodeError。
# 对宿主无所谓，对解码器子进程是致命的：worker 一崩，主进程 wait_until_ready 超时，
# 报出来的是「engine init failed — missing model files?」，跟真实原因差了十万八千里。
# 环境变量让 spawn 出来的子进程也吃这套（子进程会重新 import 本模块），reconfigure 管当前进程。
os.environ.setdefault("PYTHONIOENCODING", "utf-8")
os.environ.setdefault("PYTHONUTF8", "1")
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    except (AttributeError, ValueError):  # 被重定向成非 TextIO 时不值得为它挂掉
        pass

ENGINE_DIR = Path(__file__).resolve().parent

# GUI zip 的布局：Qwen3-TTS-GUI.exe + internal/（包本体与 llama.dll）+ output/elaborate/（音色锚点）。
# 包在 internal/ 下，所以 internal 要进 sys.path——上游的 __init__.py 还会往 PROJECT_ROOT
# （也就是 internal/）写 log/。
#
# 但必须**追加**而不是插到最前：internal/ 是 PyInstaller 的 Python 3.14 运行时（53 个 dll + 19 个
# cp314 .pyd，含 python314.dll）。排在 stdlib 前面会遮蔽本 venv 的 _ctypes 等，报
# "Module use of python314.dll conflicts with this version of Python"。追加后
# venv 的 site-packages 优先，只把 internal/ 里独有包名（qwen3_tts_gguf）当兜底。
if str(ENGINE_DIR) not in sys.path:
    sys.path.insert(0, str(ENGINE_DIR))
if str(ENGINE_DIR / "internal") not in sys.path:
    sys.path.append(str(ENGINE_DIR / "internal"))

from qwen3_tts_gguf.inference import TTSEngine, TTSConfig  # noqa: E402
from qwen3_tts_gguf.inference.schema.constants import SPEAKER_MAP  # noqa: E402

SAMPLE_RATE = 24_000
VOICE_ANCHORS = ENGINE_DIR / "output" / "elaborate"

# 必须关掉上游的流式播放：TTSConfig.streaming 默认 True，而 DecoderProxy 一构造就无条件拉起
# SpeakerWorker（workers/speaker.py 里一个裸的 sd.OutputStream，不带 device=，也就是系统默认输出
# 设备）。streaming=True 时每个解码块都会同时进 play_q（proxy.py:126），于是引擎自己也在放。
#
# 症状：本机永远多出一次「外放」，跟设置里的输出设备无关——设置成音箱就同一只音箱响两遍，
# 设置成虚拟声卡就是「音箱一遍 + 虚拟通道一遍」。我们这条链路只消费 HTTP 返回的完整 WAV，
# 流式播放对宿主毫无用处，关掉既消掉这次外放，也省掉分块解码。
OFFLINE = TTSConfig(streaming=False)


def resolve_voice(voice: str | None) -> str | None:
    """名字 → 可直接交给引擎的音色源。认不出来返回 None（调用方要报错，不能静默用默认音色）。

    三种命中：锚点路径（json/wav）→ 原样；锚点名（大小写不敏感）→ output/elaborate 下的 json；
    内置说话人 ID（vivian/serena/…）→ 原样，由上游走 spk_embd。

    最后那一路是上游的兜底逻辑：name 不在表里时 map_speaker 只打个 warning、返回 None，
    然后照样合成出一段默认音色的音频。所以「没认出来」必须在这里挡住——否则用户把音色名
    打错一个字，得到的是默认嗓子而不是错误。
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


class EngineState:
    """一个流一个音色锚点；并发请求排队串行化（引擎本质是单流推理）。"""

    def __init__(self, model_dir: str, default_voice: str | None, device: str):
        onnx_provider = {"vulkan": "DML", "cpu": "CPUExecutionProvider"}.get(device, "DML")
        # 绝对路径：解码器子进程（multiprocessing spawn）的 cwd 不保证一样。
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
        if default_voice:
            # config 只在「内置说话人 ID」那条路上被 set_voice 透传（json 锚点直接读文件，不合成）；
            # 传上是为了那种情况下合成锚点样音时不外放。
            if not self.stream.set_voice(default_voice, "你好，很高兴见到你。", config=OFFLINE):
                raise SystemExit(f"voice anchor failed for {default_voice}")
        print(
            f"tts-server ready: model={model_dir} voice={default_voice!r} device={device} "
            f"playback={'off' if not OFFLINE.streaming else 'ON'}"
        )


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

        with self.state.lock:
            try:
                if voice and voice != self.state.default_voice:
                    if not self.state.stream.set_voice(voice, text=text, config=OFFLINE):
                        self._json(400, {"error": f"voice anchor failed for {voice!r}"})
                        return

                result = self.state.stream.clone(text, language=language, config=OFFLINE)
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


def warm_up(state: "EngineState") -> None:
    """合成一句短话并丢掉。首句要付锚点预解码和后端预热的钱（实测首句 0.69s，之后稳态 0.42s），
    让这笔钱在启动时付，而不是由用户的第一句话来付。失败不致命：只是首句慢一点。"""
    started = time.monotonic()
    try:
        with state.lock:
            state.stream.clone("好的。", language="chinese", config=OFFLINE)
        print(f"tts-server warm-up done in {time.monotonic() - started:.2f}s")
    except Exception as error:  # noqa: BLE001
        print(f"tts-server warm-up skipped: {error}", file=sys.stderr)


def serve():
    parser = argparse.ArgumentParser(description="Qwen3-TTS GGUF OpenAI-compatible server")
    parser.add_argument("--model-dir", default=str(ENGINE_DIR / "model"), help="默认 engines/tts/model")
    parser.add_argument("--voice", default="Vivian", help="锚点名/路径/内置 ID；默认 Vivian（zip 自带锚点）")
    parser.add_argument("--port", type=int, default=45102)
    parser.add_argument("--device", default="vulkan", choices=["vulkan", "cpu"])
    args = parser.parse_args()

    Handler.state = EngineState(args.model_dir, args.voice, args.device)
    warm_up(Handler.state)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"tts-server listening on http://127.0.0.1:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    serve()
