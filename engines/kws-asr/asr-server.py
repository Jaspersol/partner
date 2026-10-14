"""engines/kws-asr/asr-server.py — 本仓提供的 KWS+VAD+ASR HTTP 壳。

Partner 设置里的 `asr.protocol=sensevoice` 直接用：

    POST /recognize      body: 裸 float32 PCM LE（16 kHz mono）→ { "text": "…" }
    POST /wake/infer     body: 一段任意长度的 16 kHz float32（宿主持续推送麦克风帧）；
                         引擎跑 KWS 逐帧判定，返回 { "hit": bool, "keyword"?, "confidence"? }
    GET  /wake?seq=N     长轮询唤醒流水（可选；命中历史，seq 单调）
    GET  /health

模型（scripts/install-engines.ps1 装到 engines/kws-asr/models/）：
  sensevoice/model.int8.onnx + tokens.txt   SenseVoice（非流式，30s 为限）
  kws/{encoder-epoch-12-avg-2-chunk-8-left-64.onnx, decoder…, joiner…, tokens.txt, keywords.txt}
  vad/silero_vad.onnx

架构纪律（本地音频.md）：KWS + VAD + ASR 同进程同一次 onnxruntime——本壳自己就是那个进程。
麦克风在宿主端（Partner），引擎不抢音频源：宿主把同一份采集流转发给 /wake/infer。
"""
from __future__ import annotations

import argparse
import json
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ENGINES_DIR = Path(__file__).resolve().parent
SAMPLE_RATE = 16_000

try:
    import numpy as np
    import sherpa_onnx
except ImportError as error:
    raise SystemExit(f"missing dependency: {error} — pip install -r requirements.txt")


class WakeRing:
    """KWS 命中流水：seq 单调递增；/wake?seq=N 返回 >N 的最新一条（20s 长轮询）。"""

    def __init__(self):
        self._cond = threading.Condition()
        self._events: list[dict] = []

    def push(self, keyword: str, confidence: float) -> int:
        with self._cond:
            seq = (self._events[-1]["seq"] if self._events else 0) + 1
            self._events.append(
                {"seq": seq, "keyword": keyword, "confidence": round(confidence, 4)}
            )
            if len(self._events) > 32:
                self._events = self._events[-32:]
            self._cond.notify_all()
            return seq

    def poll(self, last_seq: int, timeout: float = 20.0) -> dict:
        deadline = time.monotonic() + timeout
        with self._cond:
            while True:
                newer = [event for event in self._events if event["seq"] > last_seq]
                if newer:
                    return newer[-1]
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return {"seq": last_seq}
                self._cond.wait(min(0.5, remaining))


class Recognizers:
    """SenseVoice（VAD 兜底切分）+ KWS，一次 onnxruntime 初始化一起建。"""

    def __init__(self, models: Path, threads: int, keywords_path: Path | None):
        asr = models / "sensevoice"
        self.recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=str(asr / "model.int8.onnx"),
            tokens=str(asr / "tokens.txt"),
            num_threads=threads,  # 线程直接乘内存：压到 2
            use_itn=True,
            language="auto",
            provider="cpu",
        )

        vad_config = sherpa_onnx.VadModelConfig()
        vad_config.silero_vad.model = str(models / "vad" / "silero_vad.onnx")
        vad_config.sample_rate = SAMPLE_RATE
        vad_config.feature_dim = 128
        self.vad = sherpa_onnx.VoiceActivityDetector(vad_config, buffer_size_in_seconds=120)

        self.kws = None
        self._kws_lock = threading.Lock()
        self._kws_stream = None
        if keywords_path is not None and keywords_path.exists() and (models / "kws").is_dir():
            kws = models / "kws"
            kws_config = sherpa_onnx.KeywordSpotterConfig()
            kws_config.feat_config.sample_rate = SAMPLE_RATE
            kws_config.model_config.encoder = str(kws / "encoder-epoch-12-avg-2-chunk-8-left-64.onnx")
            kws_config.model_config.decoder = str(kws / "decoder-epoch-12-avg-2-chunk-8-left-64.onnx")
            kws_config.model_config.joiner = str(kws / "joiner-epoch-12-avg-2-chunk-8-left-64.onnx")
            kws_config.model_config.tokens = str(kws_path.parent / "tokens.txt")
            kws_config.model_config.keywords_file = str(keywords_path)
            kws_config.model_config.num_threads = threads
            kws_config.keywords_score = 1.5
            self.kws = sherpa_onnx.KeywordSpotter(kws_config.model_config)
            self._kws_stream = self.kws.create_stream()

    def recognize(self, samples: np.ndarray) -> str:
        # VAD 切 ≤30s 的保命条款：SenseVoice 只在 30s 内训练过，长输入内存近似二次增长。
        window = 30 * SAMPLE_RATE
        pieces: list[str] = []
        position = 0
        while position < len(samples):
            chunk = samples[position:position + window]
            position += window
            self.vad.accept_waveform(chunk)
            while not self.vad.is_empty:
                segment = self.vad.front
                self.vad.pop()
                stream = self.recognizer.create_stream()
                stream.accept_waveform(SAMPLE_RATE, segment.samples)
                self.recognizer.decode_stream(stream)
                pieces.append(stream.result.text.strip())
        return "".join(pieces)

    def wake_probe(self, samples: np.ndarray) -> tuple[bool, str, float]:
        """喂一段音频给 KWS，返回该段内有无命中。kws 单流推理串行。"""
        if self.kws is None or not len(samples):
            return False, "", 0.0
        with self._kws_lock:
            stream = self._kws_stream
            stream.accept_waveform(SAMPLE_RATE, samples)
            hit = False
            keyword = ""
            while self.kws.is_ready(stream):
                result = self.kws.decode(stream)
                if result.keyword:  # 命中即报（keywords.txt 的 @显示名 在 keyword 里）
                    hit = True
                    keyword = result.keyword[:64]
                    confidence = 0.9  # sherpa KWS 不带置信度：命中即事实
                    seq = HandlerShared.wake_ring.push(keyword, confidence)
                    HandlerShared.log(f"wake hit {keyword!r} seq={seq}")
                else:
                    confidence = 0.0
            if hit:
                # 复位流：命中之后从新帧开始重新听，避免一条音频里重复报。
                self._kws_stream = self.kws.create_stream()
            return hit, keyword, confidence


class HandlerShared:
    wake_ring: WakeRing = None  # type: ignore[assignment]

    @staticmethod
    def log(line: str) -> None:
        sys.stderr.write(f"asr-server: {line}\n")


class Handler(BaseHTTPRequestHandler):
    recognizers: Recognizers | None = None

    def log_message(self, fmt, *args):
        sys.stderr.write("asr-server: " + (fmt % args) + "\n")

    def do_GET(self):
        if self.path in ("/health", "/healthz"):
            self._json(200, {"ok": True})
            return
        if self.path.startswith("/wake"):
            query = dict(
                part.split("=", 1) for part in self.path[self.path.find("?") + 1:].split("&") if "=" in part
            )
            try:
                last_seq = int(query.get("seq", "0"))
            except ValueError:
                self._json(400, {"error": "bad seq"})
                return
            event = HandlerShared.wake_ring.poll(last_seq)
            self._json(200, event)
            return
        self._json(404, {"error": "not found"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length)
        samples = np.frombuffer(body, dtype="<f4")

        if self.path == "/recognize":
            try:
                text = self.recognizers.recognize(samples)
            except Exception as error:  # noqa: BLE001
                self._json(500, {"error": f"recognize failed: {error}"})
                return
            self._json(200, {"text": text})
            return

        if self.path == "/wake/infer":
            try:
                hit, keyword, confidence = self.recognizers.wake_probe(samples)
            except Exception as error:  # noqa: BLE001
                self._json(500, {"error": f"wake probe failed: {error}"})
                return
            payload = {"hit": hit}
            if hit:
                payload["keyword"] = keyword
                payload["confidence"] = confidence
            self._json(200, payload)
            return

        self._json(404, {"error": "not found"})

    def _json(self, code: int, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def main():
    parser = argparse.ArgumentParser(description="sherpa-onnx KWS+VAD+SenseVoice HTTP server")
    parser.add_argument("--models", default=str(ENGINES_DIR / "models"))
    parser.add_argument("--port", type=int, default=45103)
    parser.add_argument("--threads", type=int, default=2)
    args = parser.parse_args()

    models = Path(args.models)
    keywords = models / "kws" / "keywords.txt"
    if not (models / "sensevoice" / "model.int8.onnx").exists():
        raise SystemExit(f"missing {models / 'sensevoice' / 'model.int8.onnx'} — run scripts/install-engines.ps1 -Engine kws")

    Handler.recognizers = Recognizers(models, args.threads, keywords)
    HandlerShared.wake_ring = WakeRing()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"asr-server listening on http://127.0.0.1:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
