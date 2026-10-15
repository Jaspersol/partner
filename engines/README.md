# engines/ — 本地语音引擎（模型与运行时不进 git）

真实引擎的安装目标目录。装好后 `scripts/build-shell.sh` 把整个目录打进安装包（tauri resources），
在设置页「本地引擎进程」卡片里指向具体可执行文件即可。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1              # both
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 -Engine kws  # 只装识别
```

预算与选型理由见根目录 [本地音频.md](../本地音频.md)。一句话：TTS 占显存（~1.0–1.3GB）、
KWS+ASR 占内存（KWS 常驻 ~50MB，转写时 SenseVoice 峰值 ~500MB），两个引擎是独立进程，
关掉 = 杀进程 = 真正释放。

## 目录（实测布局）

```
engines/
  tts/
    Qwen3-TTS-GUI.exe          zip 自带的 GUI（我们不用；壳直接 import 下面的包）
    internal/                  zip 自带的 Python 3.14 运行时（53 dll + 19 cp314 .pyd）
      qwen3_tts_gguf/          推理包本体：inference/（engine/stream/llama/…）
      qwen3_tts_gguf/inference/bin/ggml-vulkan.dll 等 llm 后端（llama.cpp b10621）
    output/elaborate/          8 个音色锚点 json+wav（Vivian/晓晓/子轩/小 d/小玲玲/…）
    model/                     25 个文件，2.3GB ← hf-mirror（脚本下载）
      qwen3_tts_talker.q5_k.gguf (1006MB) · qwen3_tts_predictor.q8_0.gguf (151MB)
      qwen3_tts_decoder.fp16.onnx (230MB) · codec_encoder.fp16.onnx (114MB)
      speaker_encoder.fp16.onnx (24MB) · tokenizer.json (11MB) · embeddings/ (866MB, 19 个 npy)
    .venv/                     python 3.13 + requirements.txt（脚本建）
    tts-server.py              本仓 HTTP 壳（openai 协议 /v1/audio/speech，wav @24kHz）
    requirements.txt
  kws-asr/
    sherpa-onnx/               python -m venv 建的运行时（脚本不建，缺了会报错提示）
    models/sensevoice/         model.int8.onnx + tokens.txt        ← 脚本下载（模型 228MB，归档 999MB）
    models/kws/                encoder/decoder/joiner onnx + tokens.txt + keywords.txt
                                                               ← 脚本下载（chunk-16，~320ms 延迟）
    asr-server.py              本仓 HTTP 壳（/recognize + /wake/infer + /health）
    requirements.txt
```

TTS 的模型不在包的 release 里（`zip_release.py` 明确 `-xr!model-*`，而 repo 的 `models` tag 资产为 0）。
现成可下的是 [Solidaxel/Qwen3-TTS-GGUF](https://huggingface.co/Solidaxel/Qwen3-TTS-GGUF)，**只有 1.7B-Base**。
HF 直连本机不通，走 `hf-mirror.com`（脚本用 curl 逐个文件拉，可续传）。

## 引擎与端口

| 引擎 | 默认端口 | 宿主设置 | 引擎端点 |
|---|---|---|---|
| TTS（tts-server.py） | 45102 | `tts.protocol=openai` | `POST /v1/audio/speech` → wav |
| KWS+ASR（asr-server.py） | 45103 | `asr.protocol=sensevoice` + 唤醒门 | `POST /recognize`（裸 float32）· `POST /wake/infer` · `GET /wake?seq=` |

## 跑起来

**KWS+ASR**（先这个：下载少、不依赖显卡）

```bash
python -m venv "D:/code/Projects/partner/engines/kws-asr/sherpa-onnx"
"D:/code/Projects/partner/engines/kws-asr/sherpa-onnx/Scripts/pip" install sherpa-onnx numpy
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 -Engine kws

# 手验证：起来后 /health 应回 {"ok":true,"kws":true}
"D:/code/Projects/partner/engines/kws-asr/sherpa-onnx/Scripts/python.exe" engines/kws-asr/asr-server.py --port 45103
```

设置页填：可执行文件 = venv 的 `python.exe`，参数 = `engines/kws-asr/asr-server.py --port {port}`，端口 45103。

**TTS**（依赖 GPU；脚本一条龙：runtime zip + 25 个模型文件 + venv）

```bash
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 -Engine tts

# 手验证：起来后 /health 回 {"ok":true}，首句会慢（锚点预解码），之后 RTF ~0.25
engines/tts/.venv/Scripts/python.exe engines/tts/tts-server.py --port 45102
```

设置页：协议 `openai`，地址 `http://127.0.0.1:45102`，参数 = `engines/tts/tts-server.py --port {port}`，端口 45102。
`--voice` 默认 `Vivian`（映射到 `output/elaborate/Vivian.json`）；换音色填锚点名（晓晓 / 子轩 / 小d / 小玲玲 / 心悦 / 郭嘚嘚）。
另需 `ffmpeg` 在 PATH（从 wav/mp3 克隆时才用；用自带 json 锚点不需要）。

## 实测事实（别再照文档猜）

- **KWS 官方包只有 chunk-16**（~320ms 延迟）。壳会自动挑：有 chunk-8 优先，其次 int8。
- **`keywords.txt` 官方包里自带 8 个词**（小爱同学 / 小艺小艺 / 小米小米 …），改词才需要
  `sherpa-onnx-cli text2token --tokens kws/tokens.txt --tokens-type ppinyin raw.txt keywords.txt`；
  文件不能带 BOM。
- **`test_wavs/` 的音频对应 `test_wavs/test_keywords.txt`**，不是 `keywords.txt`——拿它们验证要换表。
- **`get_result()` 返回关键词字符串**（未命中为空串），不是结果对象。
- **不用 Silero VAD 切段**：实测把 VAD 切出来的段喂回 SenseVoice 会得到空文本或 `Yeah.` 这类乱码
  （同一段音频整块喂进去则正常），而宿主端的停顿判定早把块限在 8 s 内。壳改成按 **≤28 s 定长切分**
  ——保命条款（SenseVoice 只在 ≤30 s 训练过，长输入内存近似二次增长）仍然成立，少一个不稳定依赖。
- **下载必须校验长度**：PowerShell 的 `Invoke-WebRequest` 曾把一个 999MiB 的资源静默截成 670MB 并报成功，
  直到 `tar` 解包才发现。现在优先用 `curl.exe --fail -C -`（可续传），回退路径比对 Content-Length。
- **CPU 只用 int8**：导出包里的 `model.onnx`（894MB fp32）装完即删。
- **壳是「两个都常驻」，不是「KWS 常驻、ASR 按需」**：`Recognizers.__init__` 一次性建 SenseVoice + KWS，
  所以 idle 就是 WS **349 MB** / Private 682 MB，不是 `本地音频.md` §6 写的 20–50MB。要兑现那条纪律得改成
  首次 `/recognize` 才建 recognizer（现在没做）。转写 RTF 0.02、KWS 每 320ms slab 13ms，性能上没压力。
- **silero VAD 不再下载**；`raw.githubusercontent.com` 在本机不可达（snakers4 原始链接会失败），
  GitHub release 资产可用。

### TTS 实测（RTX 5060 Ti 16GB / 31GB RAM，2026-10-14）

- **显存 ~1.9GB**：两次实测增量 1899 / 1921 MiB（1227→3148、1157→3056）。TTS 后端确实在 GPU 上
  （日志 `using device Vulkan0 (NVIDIA GeForce RTX 5060 Ti)`，层 0–27 全部 `assigned to device Vulkan0`）。
  与上游 1.7B「~1.8GB」的说法对得上。
- **稳态 RTF 0.23–0.25**（3 次连续请求 0.53/0.58/0.54 s，2.2–2.4 s 音频）。**首句 RTF 1.23**——锚点预解码
  加后端预热，别拿第一次的数当常态。
- **进程是 3 个**：主进程（WS 2.19GB）+ 解码器 worker（525MB）+ 扬声器 worker（169MB）。解码器走
  `multiprocessing` 子进程（`DecoderProxy`），所以**杀进程要杀整棵树**，否则 worker 变孤儿继续占显存。
- **杀干净 = 全部归还**：树杀掉后显存回 1227 MiB，python 进程 0 个。这就是「关闭不占资源」的兑现方式。
- **`internal/` 必须追加到 `sys.path` 末尾，不能插到最前**：它是 PyInstaller 的 Python **3.14** 运行时
  （19 个 `cp314` .pyd + `python314.dll`）。排在最前会遮蔽 venv 的 `_ctypes`，直接报
  `Module use of python314.dll conflicts with this version of Python`。
- **上游到处 print emoji，Windows 控制台 GBK 编不了**：解码器 worker 死在
  `print('🔊 …')` 上，主进程只看到 `wait_until_ready` 超时，报的还是
  `engine init failed — missing model files?`——**误导性极强**。壳里必须
  `PYTHONIOENCODING=utf-8`（env 变量，好让 spawn 的子进程也吃到）。
- **包入口不是 `qwen3_tts_gguf`**：`__init__.py` 只导出 `logger`，要 `from qwen3_tts_gguf.inference import TTSEngine, TTSConfig`。
- **`--voice` 用锚点 json，不是内置说话人 ID**：`vivian/serena/…` 走的是 CustomVoice 的 spk_embd 路径
  （`schema/constants.py` 里 2800–3071 的表），而现成能下的只有 **1.7B-Base** 导出。壳把名字映射到
  `output/elaborate/<名字>.json`，命中不了才交回上游。
- **音色名打错不会报错，只会换一副嗓子**：上游 `map_speaker` 认不出的名字只打个 warning 返回 None，
  然后照常合成。壳因此在 `resolve_voice` 里把「认不出来」判死，请求报 400、启动参数直接 SystemExit。
  实测：`vivian`（小写，命中锚点，不重设锚点 0.32s）、`晓晓`（重设锚点）、`serena`（内置 ID，放行）、
  `vivain`（拼错）→ `HTTP 400 {"error": "unknown voice 'vivain'"}`。
- **venv 用 3.13**（上游 `requires-python >=3.14`）：推理子集在 3.13 上装得上也 import 得通
  （onnxruntime-directml 1.24.4 / gguf 0.19 / tokenizers 0.23），实测没踩到 3.14 专属语法。
  上游 `internal/` 里那份 3.14 是给 GUI exe 用的，壳不碰它。

## 大小（实测 du）

| 块 | 大小 |
|---|---|
| tts/model | 2.3 GB |
| tts/.venv | 401 MB |
| tts/internal（zip 运行时） | 311 MB |
| tts/Qwen3-TTS-GUI.exe + output | 15 MB |
| **tts 合计** | **3.0 GB** |
| kws-asr（venv + 两个模型） | 383 MB |
| **两者合计** | **3.4 GB** |

比早期估的 1.2GB 大，原因是选型从 0.6B Q4_K_M（885MB）换成了实际能下到的 **1.7B q5_k**（2445MB 模型）。
预算对照见根目录 `本地音频.md`——那里的数字对应 0.6B，已经对不上实物。
