# Partner

给电脑加一个能用嘴喊的助手：说一句话，它排队执行、回你一句语音。

**简体中文** | [繁體中文](README.zh-TW.md) | [English](README.md)

Partner 不替代编码 agent 或工作 agent。它给现有工作流加一层语音入口：用语音触发任务，配置尽量少，语音引擎和模型服务按需分体下载到本地。适合想在 Windows 桌面上“喊一声就办事”、又不想折腾一堆服务配置的人。

## 它是什么 / 不是什么

| 是 | 不是 |
|---|---|
| Windows 桌面应用，语音和文字共用一条输入队列 | 编码 agent、IDE 插件，也不替代你现有的工作 agent |
| 连接你自己的 OpenAI 兼容或 Anthropic 模型端点 | 自带大模型或云服务 |
| 本地语音引擎可选、可单独下载、可随时关掉 | 必须先下 3 GB 模型才能用的安装包 |

## 核心特点

- **单会话，按日期查看**：只有一条会话，不用新建、切换或管理多个对话；记忆和上下文始终连续。界面按日期渲染，用“年 → 月 → 日”选择器翻看有对话的日子，默认停在最新一天。
- **语音控制**：唤醒词开闸，说完自动断句、识别、入队；回复按句朗读，播放时暂停收音。
- **一站式简约配置**：一个设置页填完模型地址就能跑。端口、启动 API、令牌注入由桌面壳自动完成；模型名和音频设备从下拉里选，不用手打；改完立即生效，不用重启。
- **分体式下载本地引擎**：安装包只带两个小服务脚本。TTS（约 3.0 GB）和唤醒+识别（约 380 MB）在“设置 → 本地引擎”里各自下载，支持续传和校验；关闭即结束进程，显存和内存全部归还。

## 单会话 + 按日期渲染

所有输入（打字、语音、定时任务、子 agent 回报）都进同一条会话日志，按到达顺序执行。

- **日期只是视图**：日志不按天拆分，选哪天只决定显示哪一段。在旧日期下发消息，仍然追加到日志末尾。
- **只列有对话的日子**：没有对话的日期选不到；某天有了第一条消息会自动出现。
- **按本机日历划分**：前端把时区偏移传给服务端，“哪一天”以你所在时区为准。
- **跨午夜不跳页**：零点前开始的回合还在写时，界面停在原来那天，空闲后再切到新的一天。

## 功能概览

| 功能 | 解决什么问题 | 文档 |
|---|---|---|
| 记忆 | 记住你说过的事实和偏好，下次自动带上，可查看、改层级、删除 | [memory](doc/memory.md) |
| 子 agent | 主 agent 只对话和派活，桌面操作、读文件交给专门的子 agent 后台执行 | [sub-agents](doc/sub-agents.md) |
| 桌面操控 | 让模型看窗口、点控件、打字、启动程序，每步可审批 | [desktop-control](doc/desktop-control.md) |
| 语音功能 | 唤醒、识别、朗读整条链路，可接本地引擎或外部服务 | [voice](doc/voice.md) |
| 定时 | 到点把一句话当成你发的消息投进队列，你或助手都能建 | [schedules](doc/schedules.md) |
| MCP | 接入 stdio MCP 服务器，工具默认逐次询问 | [mcp](doc/mcp.md) |
| Skills | 把常用做法写成 `SKILL.md`，上传或新建后，技能列表自动进入模型提示 | [skills](doc/skills.md) |

## 快速启动

### 前置条件

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 2004 及以上 |
| .NET | .NET 8 SDK |
| Node | Node 22 + npm（不要用 pnpm，esbuild 会装不上） |
| Rust | cargo 1.98+（只有桌面壳需要） |
| WebView2 | Win10+ 自带 |
| 模型 | 一个 OpenAI 兼容或 Anthropic 端点，例如本地 Ollama 或 Claude |

### 安装与启动

```bash
npm --prefix ui install          # 首次
npx --prefix ui tauri dev        # 启动前端 + 桌面壳，壳会自动拉起 API
```

启动后在“设置 → 对话模型”填服务地址并选模型。没配模型时队列不会执行，界面会写明缺什么。也可以用环境变量：

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...   # 可选
export PARTNER_LLM_PROTOCOL=openai   # 可选：openai / anthropic
```

### 打安装包

```bash
bash scripts/build-shell.sh      # 发布 API → 构建前端 → 编译壳 → 打 NSIS 安装包
```

### 只跑测试

```bash
bash scripts/ci.sh               # 编译、单测、smoke、能力门禁、前端、壳
SKIP_SHELL=1 bash scripts/ci.sh  # 跳过最慢的壳编译
```

## 文档导航

完整目录见 [doc/README.md](doc/README.md)，包括开发调试方式和每个功能的配置说明。

## 项目结构

| 路径 | 内容 |
|---|---|
| `src/Partner.Core` | 会话、队列、agent 回合、记忆、调度、MCP、Skills、治理 |
| `src/Partner.Desktop` | 窗口枚举、截图、输入注入、动作校验 |
| `src/Partner.Voice` | 麦克风输入、断句、ASR/TTS 客户端、播放 |
| `src/Partner.Api` | 本地 HTTP API、队列消费者、语音宿主、本地引擎管理 |
| `ui/` | Vue 3 前端（简中 / 繁中 / 英文界面） |
| `src-tauri/` | Tauri 2 桌面壳 |
| `engines/` | 本地语音引擎的服务脚本；运行时和模型不进 git |
| `scripts/` | CI、打包、引擎安装脚本 |

## 常见问题

**必须下载本地语音引擎吗？** 不必。文字输入不依赖语音；语音也可以接你已有的 ASR/TTS 服务地址。

**数据存在哪？** 默认 `%LocalAppData%/Partner/workspace`：会话日志、队列、记忆库、设置都在这里。记忆库是唯一存储，删除即丢失。

**支持 macOS / Linux 吗？** 不支持。桌面操控和音频都依赖 Win32 接口。

## 许可

Partner 的代码用 [MIT](LICENSE) 许可。语音引擎的运行环境、模型权重和音色锚点不在本仓库，按 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 里列的各自许可单独发布（Apache-2.0、FunASR MODEL_LICENSE 等），MIT 不覆盖它们。
