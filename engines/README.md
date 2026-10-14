# engines/ — 本地语音引擎（可选，不进 git）

真实引擎的安装目标目录。默认流程：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install-engines.ps1 [-Engine tts|kws|both]
```

装好后 `build-shell.sh` 会把整个目录打进安装包（tauri resources），设置里的
「本地引擎进程」卡片指向下面的可执行文件即可——表中给出推荐配置。

## 目录约定

```
engines/
  tts/
    qwen3_tts_gguf/           HaujetZhao/Qwen3-TTS-GGUF（Vulkan 预编译 + Python 运行时）
    models/                   0.6B GGUF（talker / tokenizer）
    tts-server.py             本仓提供的 HTTP 壳（openai 协议 /v1/audio/speech）
    requirements.txt
  kws-asr/
    sherpa/                   sherpa-onnx Python 运行时（pip 安装后打包的 venv）
    models/                   SenseVoice int8 ONNX + KWS zipformer + VAD
    asr-server.py             本仓提供的 HTTP 壳（/recognize + /wake 长轮询）
    requirements.txt
```

## 引擎与端口

| 引擎 | port 占位 | 宿主协议 | 引擎端点 |
|---|---|---|---|
| TTS（tts-server.py） | 45102 | `tts.protocol=openai` | `POST /v1/audio/speech`（wav） |
| KWS+ASR（asr-server.py） | 45103 | `asr.protocol=sensevoice` + 唤醒门 | `POST /recognize`（裸 float32）+ `GET /wake?seq=` |

显存/内存预算见根目录 本地音频.md。

## 大小

TTS：~130MB（运行时）+ ~700MB（0.6B GGUF）≈ 850MB
KWS+ASR：~200MB（运行时）+ ~240MB（模型）≈ 450MB
都装 ≈ 1.3GB，安装包超过 1GB 是正常的。
