# 第三方组件与模型许可 / Third-party notices

Partner 应用的**代码**以 MIT 许可发布，见 [LICENSE](LICENSE)。这个许可**不覆盖**语音引擎的运行时、模型权重和音色锚点：它们不在本仓库里，单独作为 GitHub release 资产（tag `engines-v1`）分发，每个包内都有 `NOTICE/` 目录，包含完整许可文本、上游来源和改动声明。

The application code is MIT licensed, see [LICENSE](LICENSE). That licence does **not** cover the voice engines: their runtimes, model weights and voice anchors are not in this repository. They ship as GitHub release assets under the `engines-v1` tag, and every pack carries a `NOTICE/` folder with the full licence texts, upstream provenance and a statement of changes.

## 应用本体 / The app

| 组件 | 许可 | 说明 |
|---|---|---|
| Tauri 2（`src-tauri/`） | MIT OR Apache-2.0 | 桌面壳、插件 |
| Vue 3、Vite、vue-tsc、Vitest（`ui/`） | MIT | 前端 |
| .NET 8 / ASP.NET Core（`src/`） | MIT | sidecar 运行时随安装包 |
| Microsoft.Data.Sqlite | MIT | SQLite 绑定；SQLite 本身为 Public Domain |
| AngleSharp | MIT | HTML 解析 |
| NAudio | MIT | 音频设备 |
| WebView2 Runtime | Microsoft 分发条款 | 由 Windows/Tauri 提供，不随包捆绑 |
| NSIS | zlib/libpng | 安装包生成 |

直接依赖见上表；完整清单是 `src-tauri/Cargo.lock`、`ui/package-lock.json` 和 `src/*/*.csproj`。

## 引擎包 / The engine packs

| 资产 | 包含 | 许可 |
|---|---|---|
| `partner-engine-tts-a-runtime.zip` | embeddable CPython 3.13.9、`tts-server.py`、`tts/internal/`（Qwen3-TTS-GGUF GUI 运行时）、音色锚点 | PSF-2.0 / MIT（本项目）/ MIT（HaujetZhao）/ Apache-2.0（Qwen3-TTS 衍生物） |
| `partner-engine-tts-b-talker.zip` | `qwen3_tts_talker.q5_k.gguf`、`qwen3_tts_predictor.q8_0.gguf` | Apache-2.0 |
| `partner-engine-tts-c-codec.zip` | decoder / codec encoder / speaker encoder（ONNX fp16）、`tokenizer.json`、`embeddings/` | Apache-2.0 |
| `partner-engine-kws.zip` | embeddable CPython、`asr-server.py`、sherpa-onnx 运行时、SenseVoiceSmall、KWS zipformer、Silero VAD | PSF-2.0 / MIT（本项目）/ Apache-2.0 / FunASR MODEL_LICENSE / Apache-2.0 / MIT |

上游与改动：

| 上游 | 许可 | 我方改动 |
|---|---|---|
| [QwenLM/Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) — `Qwen/Qwen3-TTS-12Hz-1.7B-Base` | Apache-2.0 | 无（权重未修改） |
| [Solidaxel/Qwen3-TTS-GGUF](https://huggingface.co/Solidaxel/Qwen3-TTS-GGUF) | Apache-2.0 | 上游已做 GGUF / ONNX 量化，我方原样再分发 |
| [HaujetZhao/Qwen3-TTS-GGUF](https://github.com/HaujetZhao/Qwen3-TTS-GGUF) | MIT | 原样再分发其 GUI 运行时（`internal/`）与转换工具产物 |
| [FunAudioLLM/SenseVoice](https://github.com/FunAudioLLM/SenseVoice) — SenseVoiceSmall int8 | 代码 MIT；权重 FunASR MODEL_LICENSE v1.1 | 由 [k2-fsa 导出](https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models) 的 ONNX int8，我方原样再分发 |
| [k2-fsa/sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) | Apache-2.0 | 无 |
| KWS zipformer（`pkufool` 训练，经 k2-fsa 发布） | Apache-2.0 | 无 |
| [snakers4/silero-vad](https://github.com/snakers4/silero-vad) | MIT | 无 |
| CPython 3.13 embeddable | PSF-2.0 | 加入已安装的 wheel 与 `._pth` |
| PyInstaller（`internal/` 的打包器） | GPL-2.0 **+ Bootloader Exception** | 例外条款允许用任意许可分发打包产物，见 `third-party/PyInstaller-COPYING.txt` |

`third-party/` 保存这些许可的原文，打包脚本把整目录复制进每个包的 `NOTICE/LICENSES/`。第三方 wheel 的许可清单由打包脚本从各包 `*.dist-info/METADATA` 生成，写入 `NOTICE/THIRD-PARTY-LICENSES-<包名>.txt`。四个包都会解压进同一个 `engines/` 目录，所以每包的 `MODEL_CARD` 与清单带包名后缀，脚本会检查 `NOTICE/` 下没有重名文件。

## 义务与落实 / Obligations and where they are met

| 条款 | 要求 | 落实 |
|---|---|---|
| Apache-2.0 §4(a) | 随分发提供许可副本 | `NOTICE/LICENSES/Apache-2.0.txt`（每个含 Apache 代码的包） |
| Apache-2.0 §4(b) | 修改过的文件要标注 | `NOTICE/MODEL_CARD-<包名>.md` 的 "Modifications" 段（每包一份，不会互相覆盖） |
| Apache-2.0 §4(d) | 若上游带 NOTICE，需保留 | 上游 Qwen3-TTS / sherpa-onnx 不带独立 NOTICE 文件，已在 MODEL_CARD 记来源 |
| FunASR MODEL_LICENSE §2.2 | 署名 + 保留模型名 | `NOTICE/MODEL_CARD-kws.md` 保留 "SenseVoice"、"SenseVoiceSmall" 名称与 Alibaba Group / FunAudioLLM 署名，并附许可全文 |
| MIT（Silero VAD、Qwen3-TTS-GGUF、本项目脚本） | 版权声明 + 许可文本 | `NOTICE/LICENSES/` |
| PSF-2.0 | Python 许可随运行时 | `NOTICE/LICENSES/PSF-Python-3.13.txt` |
| PyInstaller GPL-2.0 + Exception | 分发打包产物时保留其许可与例外说明 | `NOTICE/LICENSES/PyInstaller-COPYING.txt` |

## 检查结论 / Review notes

- **MIT 只覆盖本仓库代码**：README、本文件和应用内 `notices/` 都把这一点写明，避免下游误以为权重也是 MIT。
- **SenseVoice 权重**：FunASR MODEL_LICENSE v1.1 §2.1 授予使用、复制、修改、分享；§2.2 要求署名并保留模型名；§3 的 "provided for reference and learning purposes only" 出现在 "Responsibility and Risk"（免责）章节，与后半句"阿里巴巴不对使用后果承担责任"同属免责表述，不是使用范围限制；上游维护者在 [issue #287](https://github.com/FunAudioLLM/SenseVoice/issues/287) 也已书面确认允许商业使用、条件即署名与保留模型名。我方按 §2.1/§2.2 执行：不改权重、署名、保留模型名、附全文。
- **音色锚点**：`output/elaborate/*.json` 与 `.wav` 由 Qwen3-TTS 的内置音色导出（Apache-2.0 权重的衍生物），不含第三方真人录音，也不含用户克隆的音色。用户自己克隆的音色只留在本机 `engines/tts/output/`，不进仓库、不进发布包。
- **不再分发的部分**：`engines/tts/.venv`、`engines/kws-asr/sherpa-onnx` 这些 venv 只在开发机存在，发布包用重新打包的 embeddable 解释器替代；`test-output.wav`、`__pycache__`、`internal/log/`（含本机路径的运行日志）不进包——打包脚本对后三类做了过滤，并在生成后逐包检查。
- **未使用的许可**：仓库与发布包内没有 GPL/AGPL 代码参与链接。PyInstaller 的 GPL 由 Bootloader Exception 覆盖（见上表），不传染打包产物。
- **`libsndfile`（唯一的 LGPL 组件）**：`soundfile` 包自带 `libsndfile_x64.dll`，LGPL-2.1。它是独立 DLL、运行时动态加载，因此满足 LGPL 对「可替换/可重链接」的要求；许可文本在 `NOTICE/LICENSES/packages/LGPL-2.1-libsndfile.txt`，`_soundfile_data/COPYING` 也随包保留。源码见 https://github.com/libsndfile/libsndfile 。

## 报错与联系 / Reporting

发现遗漏的署名或许可文本，请开 issue 或直接 PR：许可文本放在 `third-party/`，打包脚本会自动带入每个包。
