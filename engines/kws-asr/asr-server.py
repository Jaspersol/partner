"""engines/kws-asr/asr-server.py — the KWS+ASR HTTP shell shipped with this repo.

Used directly by Partner's `asr.protocol=sensevoice` setting:

    POST /recognize      body: raw float32 PCM LE (16 kHz mono) -> { "text": "..." }
    POST /wake/infer     body: a slice of 16 kHz float32 (the host forwards microphone frames)
                         -> { "hit": bool, "keyword"?, "confidence"? }
    GET  /wake?seq=N     long-poll wake events (optional; seq is monotonic)
    GET  /health

Models (scripts/install-engines.ps1 -Engine kws downloads them to engines/kws-asr/models/):
  sensevoice/model.int8.onnx + tokens.txt      SenseVoice (offline, split into fixed slices of at most 28 s)
  kws/encoder-*.onnx + decoder-* + joiner-*    zipformer KWS (this package is chunk-16; chunk-8 wins if present)
  kws/keywords.txt                             wake word table (pinyin tokens; the display name follows @)

Architecture rule: KWS, VAD and ASR share one process and one onnxruntime initialization — this shell is that
process. The microphone stays on the host side (Partner), so the engine never claims the audio source: the host
forwards the same capture to /wake/infer.
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
    """Pick one encoder/decoder/joiner set. chunk-8 (about 160 ms) before chunk-16 (about 320 ms), and
    int8 (faster on CPU) within a chunk. All three files must share a suffix or sherpa refuses to load."""
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
    """KWS hit stream: seq increases monotonically; /wake?seq=N returns the newest event above N (20 s poll)."""

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
    """SenseVoice and KWS, built together in one onnxruntime initialization."""

    def __init__(self, models: Path, threads: int, keywords_path: Path | None, wake_ring: WakeRing) -> None:
        self.wake_ring = wake_ring
        self.keywords_path = keywords_path

        # SenseVoice is built lazily: it dominates idle memory (about 330 MB private) while only KWS listens
        # most of the time. The first real transcription builds it and it stays resident afterward.
        self._asr_dir = models / "sensevoice"
        self._recognizer = None
        self._recognizer_lock = threading.Lock()
        self.threads = threads

        self.kws = None
        self._kws_lock = threading.Lock()
        self._kws_stream = None
        self.models = models
        self.threads = threads
        self.picked = pick_kws_files(models / "kws") if keywords_path is not None else None
        if keywords_path is not None and keywords_path.exists():
            self._build_kws(keywords_path.read_text(encoding="utf-8"))

    @property
    def recognizer(self):
        """Build SenseVoice on first use. Double-checked locking so concurrent first requests build one copy."""
        if self._recognizer is None:
            with self._recognizer_lock:
                if self._recognizer is None:
                    started = time.monotonic()
                    self._recognizer = sherpa_onnx.OfflineRecognizer.from_sense_voice(
                        model=str(self._asr_dir / "model.int8.onnx"),  # int8 on CPU; fp16 only pays off on CUDA
                        tokens=str(self._asr_dir / "tokens.txt"),
                        num_threads=self.threads,  # Threads multiply memory, so keep this low.
                        use_itn=True,
                        language="auto",
                        provider="cpu",
                    )
                    print(f"sensevoice loaded on first use in {time.monotonic() - started:.1f}s", file=sys.stderr)
        return self._recognizer

    @property
    def asr_loaded(self) -> bool:
        return self._recognizer is not None

    def _build_kws(self, keywords_content: str) -> None:
        """Rebuild the spotter and listening stream from this keyword content. Takes about 1 s, during which
        /wake/infer callers queue on the lock. The binding cannot take an in-memory word list (extra keywords
        on create_stream are per-stream and not persistent), so the content goes to disk first."""
        keywords_content = keywords_content.strip()
        self.keywords_path.write_text(keywords_content + "\n", encoding="utf-8", newline="\n")
        if self.picked is None:
            return
        encoder, decoder, joiner = self.picked
        spotter = sherpa_onnx.KeywordSpotter(
            tokens=str(self.models / "kws" / "tokens.txt"),
            encoder=str(encoder),
            decoder=str(decoder),
            joiner=str(joiner),
            keywords_file=str(self.keywords_path),
            num_threads=self.threads,
            keywords_score=1.5,
            keywords_threshold=0.25,
            provider="cpu",
        )
        with self._kws_lock:
            self.kws = spotter
            self._kws_stream = spotter.create_stream()
            print(f"kws rebuilt: {len(keywords_content.splitlines())} keyword lines", file=sys.stderr)

    def recognize(self, samples: np.ndarray) -> str:
        """Transcribe in fixed-size slices. The host already splits on pauses (within WindowSeconds); the
        splitting here is a safety net for anything longer.

        Silero VAD is deliberately not used for segmentation: feeding VAD segments back into SenseVoice
        produced empty text or garbled output in testing, while whole blocks transcribe correctly, and the
        host's pause detection already keeps blocks under 8 s. One fewer unstable dependency and one less
        alteration of the audio. SenseVoice was trained only up to 30 s and memory grows roughly
        quadratically beyond that (611 s took 5.5 GB).
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
        """Feed a slice of audio to KWS and collect hits. Single-stream inference runs under a lock."""
        if self.kws is None or not len(samples):
            return False, "", 0.0

        with self._kws_lock:
            stream = self._kws_stream
            stream.accept_waveform(SAMPLE_RATE, np.asarray(samples, dtype="f4"))

            hit = False
            keyword = ""
            while self.kws.is_ready(stream):
                self.kws.decode_stream(stream)
                # This binding returns the keyword string itself (empty on no hit), not a result object.
                text = self.kws.get_result(stream)
                if text:
                    hit = True
                    # sherpa strips the @display-name marker from keywords.txt, so this is the display name.
                    keyword = text.strip()
                    self.wake_ring.push(keyword, 1.0)
                    print(f"wake hit {keyword!r}", file=sys.stderr)

            if hit:
                # Start a new stream after a hit: the old stream keeps its matched prefix state.
                self._kws_stream = self.kws.create_stream()
            return hit, keyword, 1.0 if hit else 0.0

    def keywords_now(self) -> list[str]:
        """Display names of the current keyword list; empty when KWS is off."""
        if self.kws is None or self.keywords_path is None or not self.keywords_path.exists():
            return []
        return [
            line.split("@", 1)[1].strip()
            for line in self.keywords_path.read_text(encoding="utf-8").splitlines()
            if "@" in line
        ]

    def set_keywords(self, words: list[str]) -> None:
        """Replace the keyword list: display names to pinyin, write to disk, rebuild the spotter."""
        content = format_keywords(words)
        if content is None:
            raise ValueError("没有一个可用唤醒词（词须能转拼音）")
        if self.picked is None:
            # KWS never started: the words are on disk and take effect on the next launch.
            self.keywords_path.write_text(content + "\n", encoding="utf-8", newline="\n")
            return
        self._build_kws(content)


class HandlerShared:
    wake_ring: WakeRing
    recognizers: "Recognizers"


def keywords_pinyin(texts: list[str]) -> list[list[str]]:
    """Display name -> pinyin token line. Uses upstream text2token (ppinyin, the same scheme as the keyword
    table) instead of assembling pinyin here: the model matches the symbols in this token table, so a
    matching scheme is what makes hits reliable."""
    from sherpa_onnx import text2token

    return [
        " ".join(tokens)
        for tokens in text2token(
            texts, str(ENGINES_DIR / "models" / "kws" / "tokens.txt"), tokens_type="ppinyin"
        )
    ]


def format_keywords(words: list[str]) -> str | None:
    """Word list -> KWS keyword file format (pinyin line @display name). Returns None when every entry is
    empty or non-Chinese, so the caller answers 400."""
    rows: list[str] = []
    for word, encoded in zip(words, keywords_pinyin(words)):
        if not word.strip() or not encoded:
            continue
        rows.append(f"{encoded} @{word.strip()}")
    return "\n".join(rows) if rows else None


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        sys.stderr.write("asr-server: " + (fmt % args) + "\n")

    def do_GET(self):
        if self.path in ("/health", "/healthz"):
            self._json(200, {"ok": True, "kws": HandlerShared.recognizers.kws is not None, "asr": HandlerShared.recognizers.asr_loaded})
            return
        if self.path == "/keywords":
            self._json(200, {"keywords": HandlerShared.recognizers.keywords_now()})
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

    def do_PUT(self):
        # A keyword update replaces the whole list, so PUT fits REST convention; POST is accepted as well.
        if self.path == "/keywords":
            self._do_keywords()
            return
        self._json(404, {"error": "not found"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length)
        samples = np.frombuffer(body, dtype="<f4")

        if self.path == "/recognize":
            try:
                text = HandlerShared.recognizers.recognize(samples)
            except Exception as error:  # noqa: BLE001 - Engine errors must become 5xx, not kill the process.
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

        if self.path == "/keywords":
            self._do_keywords()
            return

        self._json(404, {"error": "not found"})

    def _do_keywords(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) or b"{}"
        try:
            request = json.loads(raw)
        except json.JSONDecodeError:
            self._json(400, {"error": "bad json"})
            return

        words = request.get("keywords")
        if not isinstance(words, list) or any(not isinstance(w, str) for w in words):
            print(f"keywords PUT rejected: type={type(words).__name__} body={raw[:200]!r}", file=sys.stderr)
            self._json(400, {"error": "keywords must be a list of strings"})
            return

        try:
            HandlerShared.recognizers.set_keywords(words)
        except Exception as error:  # noqa: BLE001 - A keyword failure must answer 4xx/5xx, never silently pass.
            message = str(error)
            code = 400 if isinstance(error, ValueError) else 500
            self._json(code, {"error": message})
            return
        self._json(200, {"keywords": HandlerShared.recognizers.keywords_now()})

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
