"""engines/kws-asr/asr-server.py — 本仓提供的 KWS+ASR HTTP 壳。

Partner 设置里的 `asr.protocol=sensevoice` 直接用：

    POST /recognize      body: 裸 float32 PCM LE（16 kHz mono）→ { "text": "…" }
    POST /wake/infer     body: 一段 16 kHz float32（宿主把麦克风帧转发过来）
                         → { "hit": bool, "keyword"?, "confidence"? }
    GET  /wake?seq=N     长轮询唤醒流水（可选；seq 单调）
    GET  /health

模型（scripts/install-engines.ps1 -Engine kws 下载到 engines/kws-asr/models/）：
  sensevoice/model.int8.onnx + tokens.txt      SenseVoice（非流式，按 ≤28 s 定长切分）
  kws/encoder-*.onnx + decoder-* + joiner-*    zipformer KWS（本包是 chunk-16；chunk-8 若有会优先）
  kws/keywords.txt                             唤醒词表（拼音 token，@后是显示名）

架构纪律（本地音频.md）：KWS + VAD + ASR 同进程同一次 onnxruntime 初始化——本壳自己就是那个进程。
麦克风在宿主端（Partner），引擎不抢音频源：宿主把同一份采集转发给 /wake/infer。
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
except ImportError as error:  # pragma: no cover - startup guard
    raise SystemExit(f"missing dependency: {error} — pip install -r requirements.txt")


def pick_kws_files(kws_dir: Path) -> tuple[Path, Path, Path] | None:
    """挑一组 encoder/decoder/joiner。优先 chunk-8（~160 ms），退 chunk-16（~320 ms）；
    同一 chunk 下优先 int8（CPU 上更快）。三件必须同后缀，否则 sherpa 拒载。"""
    encoders = sorted(kws_dir.glob("encoder-*.onnx"))
    if not encoders:
        return None

    def rank(path: Path) -> tuple[int, int]:
        chunk = 0 if "chunk-8" in path.name else 1
        quant = 0 if path.name.endswith(".int8.onnx") else 1
        return (chunk, quant)

    encoder = min(encoders, key=rank)
    base = encoder.name.replace("encoder-", "", 1)
    decoder = kws_dir / f"decoder-{base}"
    joiner = kws_dir / f"joiner-{base}"
    if not decoder.exists() or not joiner.exists():
        return None
    return encoder, decoder, joiner


class WakeRing:
    """KWS 命中流水：seq 单调递增；/wake?seq=N 返回 >N 的最新一条（20 s 长轮询）。"""

    def __init__(self) -> None:
        self._cond = threading.Condition()
        self._events: list[dict] = []

    def push(self, keyword: str, confidence: float) -> int:
        with self._cond:
            seq = (self._events[-1]["seq"] if self._events else 0) + 1
            self._events.append({"seq": seq, "keyword": keyword, "confidence": round(confidence, 4)})
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
    """SenseVoice + KWS，一次 onnxruntime 初始化一起建。"""

    def __init__(self, models: Path, threads: int, keywords_path: Path | None, wake_ring: WakeRing) -> None:
        self.wake_ring = wake_ring

        asr_dir = models / "sensevoice"
        self.recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
            model=str(asr_dir / "model.int8.onnx"),  # CPU 用 int8：fp16 在 CUDA 才有意义
            tokens=str(asr_dir / "tokens.txt"),
            num_threads=threads,  # 线程直接乘内存：压到 2
            use_itn=True,
            language="auto",
            provider="cpu",
        )

        self.kws = None
        self._kws_lock = threading.Lock()
        self._kws_stream = None
        if keywords_path is not None and keywords_path.exists():
            picked = pick_kws_files(models / "kws")
            if picked is not None:
                encoder, decoder, joiner = picked
                self.kws = sherpa_onnx.KeywordSpotter(
                    tokens=str(models / "kws" / "tokens.txt"),
                    encoder=str(encoder),
                    decoder=str(decoder),
                    joiner=str(joiner),
                    keywords_file=str(keywords_path),
                    num_threads=threads,
                    keywords_score=1.5,
                    keywords_threshold=0.25,
                    provider="cpu",
                )
                self._kws_stream = self.kws.create_stream()
                print(f"kws: {encoder.name} keywords={keywords_path.name}", file=sys.stderr)

    def recognize(self, samples: np.ndarray) -> str:
        """定长切分转写。宿主已按停顿切过块（≤ WindowSeconds），这里的切分是 30 s 保命条款。

        <para>
        刻意不用 Silero VAD 切段：实测把 VAD 切出来的段喂回 SenseVoice 会得到空文本或乱码
        （整段喂进去则正常），而宿主端的停顿判定早就把块限在 8 s 内。少一个不稳定依赖，
        也少一层对音频的改动——转写质量的边界由「宿主切块」和「这里强制 ≤28 s」两处说了算。
        SenseVoice 只在 ≤30 s 上训练过，超过会内存近似二次增长（实测 611 s → 5.5 GB）。
        </para>
        """
        samples = np.asarray(samples, dtype="f4").reshape(-1)
        if samples.size == 0:
            return ""

        pieces: list[str] = []
        window = 28 * SAMPLE_RATE
        for start in range(0, samples.size, window):
            chunk = np.ascontiguousarray(samples[start:start + window])
            stream = self.recognizer.create_stream()
            stream.accept_waveform(SAMPLE_RATE, chunk)
            self.recognizer.decode_stream(stream)
            pieces.append(stream.result.text.strip())

        return "".join(pieces)

    def wake_probe(self, samples: np.ndarray) -> tuple[bool, str, float]:
        """喂一段音频给 KWS 并取走命中。单流推理串行（锁）。"""
        if self.kws is None or not len(samples):
            return False, "", 0.0

        with self._kws_lock:
            stream = self._kws_stream
            stream.accept_waveform(SAMPLE_RATE, np.asarray(samples, dtype="f4"))

            hit = False
            keyword = ""
            while self.kws.is_ready(stream):
                self.kws.decode_stream(stream)
                # 这个绑定返回的就是关键词字符串（未命中为空串），不是结果对象——实测。
                text = self.kws.get_result(stream)
                if text:
                    hit = True
                    # keywords.txt 里的 @显示名 由 sherpa 解析掉，这里拿到的就是显示名。
                    keyword = text.strip()
                    self.wake_ring.push(keyword, 1.0)
                    print(f"wake hit {keyword!r}", file=sys.stderr)

            if hit:
                # 命中之后换一条流重新听：同一条流会一直持有已匹配的前缀状态。
                self._kws_stream = self.kws.create_stream()
            return hit, keyword, 1.0 if hit else 0.0


class HandlerShared:
    wake_ring: WakeRing
    recognizers: "Recognizers"


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        sys.stderr.write("asr-server: " + (fmt % args) + "\n")

    def do_GET(self):
        if self.path in ("/health", "/healthz"):
            self._json(200, {"ok": True, "kws": HandlerShared.recognizers.kws is not None})
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
            self._json(200, HandlerShared.wake_ring.poll(last_seq))
            return
        self._json(404, {"error": "not found"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length)
        samples = np.frombuffer(body, dtype="<f4")

        if self.path == "/recognize":
            try:
                text = HandlerShared.recognizers.recognize(samples)
            except Exception as error:  # noqa: BLE001 - 引擎异常必须变成 5xx，而不是杀进程
                self._json(500, {"error": f"recognize failed: {error}"})
                return
            self._json(200, {"text": text})
            return

        if self.path == "/wake/infer":
            try:
                hit, keyword, confidence = HandlerShared.recognizers.wake_probe(samples)
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
    parser.add_argument("--keywords", default=None, help="唤醒词表；默认 models/kws/keywords.txt（存在才开 KWS）")
    args = parser.parse_args()

    models = Path(args.models)
    if not (models / "sensevoice" / "model.int8.onnx").exists():
        raise SystemExit(
            f"missing {models / 'sensevoice' / 'model.int8.onnx'} — run scripts/install-engines.ps1 -Engine kws"
        )

    keywords = Path(args.keywords) if args.keywords else models / "kws" / "keywords.txt"
    HandlerShared.wake_ring = WakeRing()
    HandlerShared.recognizers = Recognizers(models, args.threads, keywords, HandlerShared.wake_ring)

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"asr-server listening on http://127.0.0.1:{args.port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
