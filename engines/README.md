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
    qwen3_tts_gguf/            HaujetZhao/Qwen3-TTS-GGUF 的 Vulkan 预编译包（脚本解压）
    model/                     六个导出产物：talker.q5_k.gguf / predictor.q8_0.gguf /
                               decoder.fp16.onnx / codec_encoder.fp16.onnx /
                               speaker_encoder.fp16.onnx / tokenizer.json   ← 手动放
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

**TTS**（依赖 GPU 与导出产物）

```bash
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 -Engine tts
# 然后把六个模型文件放进 engines/tts/model/（脚本会列出缺哪些）
python -m venv engines/tts/.venv
engines/tts/.venv/Scripts/pip install -r engines/tts/requirements.txt
```

设置页：协议 `openai`，参数 = `engines/tts/tts-server.py --model-dir model --voice vivian --port {port}`，端口 45102。
另需 `ffmpeg` 在 PATH（参考音频克隆用）。

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
- **silero VAD 不再下载**；`raw.githubusercontent.com` 在本机不可达（snakers4 原始链接会失败），
  GitHub release 资产可用。

## 大小

TTS：~130MB（运行时）+ ~700MB（0.6B GGUF）≈ 850MB
KWS+ASR：~40MB（venv）+ ~250MB（模型）≈ 300MB
都装 ≈ 1.2GB，安装包超过 1GB 是正常的。
