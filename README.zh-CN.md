<div align="center">

# Partner

一个开箱即用的单会话Windows桌面AI助手

[![Version](https://img.shields.io/github/v/release/Jaspersol/partner?color=blue&label=version)](https://github.com/Jaspersol/partner/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-lightgrey.svg)](https://github.com/Jaspersol/partner/releases)
[![Built with Tauri](https://img.shields.io/badge/built%20with-Tauri%202-orange.svg)](https://tauri.app/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

**简体中文** | [繁體中文](README.zh-TW.md) | [English](README.md)

**[下载安装](#下载安装) · [上手](#上手) · [功能](#功能) · [常见问题](#常见问题) · [文档](doc/README.md)**

</div>

## 简介

Partner 是一个支持语音输入输出的单会话智能助手，连接你配置的 OpenAI 兼容或 Anthropic 端点。它具备常规 harness 的大多数功能：

- **单会话**：只有一条会话，不用新建和切换，见[单会话](#单会话)。
- **语音**：唤醒词、断句、识别、逐句朗读、语音审批。可以下载本地引擎，也可以接已有的 ASR/TTS 服务。
- **桌面操控和子 agent**：需要操作窗口或读文件的请求交给子 agent 在后台执行，默认动手前先征求你同意。
- **记忆、定时、MCP、Skills**：见[功能](#功能)。

## 单会话

Partner 只有一条会话。打字、语音、定时任务和子 agent 回报都写进同一条日志，按到达顺序执行。

- **无需维护**：不用新建、命名、切换或清理会话。
- **心智负担小**：不用记某件事在哪个会话里聊过，想到什么直接说。
- **适合语音**：开口前不需要先选会话。

界面按日期分页查看，日期选择器只决定看哪一天。停在旧日期时发的消息照样追加到日志末尾；没有消息的日期不显示。日期按本机时区划分，零点时如果回合还没结束，界面等它结束再切到新的一天。

## 截图

<table>
<tr>
<td align="center" width="50%"><img src="doc/images/main-zh.png" alt="主界面：会话、工具调用卡片和输入框" width="400"><br>主界面</td>
<td align="center" width="50%"><img src="doc/images/approval-zh.png" alt="桌面操控执行前的批准提示" width="400"><br>操作前批准</td>
</tr>
<tr>
<td align="center"><img src="doc/images/memory-zh.png" alt="记忆页：分层、置顶和删除" width="400"><br>记忆</td>
<td align="center"><img src="doc/images/schedules-zh.png" alt="定时任务列表" width="400"><br>定时任务</td>
</tr>
<tr>
<td align="center"><img src="doc/images/model-zh.png" alt="设置：对话模型" width="400"><br>模型设置</td>
<td align="center"><img src="doc/images/engines-zh.png" alt="设置：本地语音引擎下载" width="400"><br>本地引擎</td>
</tr>
</table>

## 下载安装

### 系统要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 2004 及以上，x64 |
| WebView2 | Windows 10/11 自带 |
| 模型 | 一个 OpenAI 兼容或 Anthropic 端点 |
| 语音（可选） | 麦克风和扬声器；本地朗读需要约 1.9 GB 显存，唤醒和识别只用 CPU |

安装包自带 .NET 运行时。

### 安装

1. 从 [Releases](https://github.com/Jaspersol/partner/releases/latest) 下载 `Partner_<版本>_x64-setup.exe`。
2. 运行安装程序，装到当前用户目录，不需要管理员权限。
3. 安装包没有代码签名，SmartScreen 拦截时点“更多信息 → 仍要运行”。可以用同一 release 里的 `SHA256SUMS.txt` 校验文件。

### 更新

“设置 → 通用 → 检查更新”。应用下载新安装包，核对 SHA-256 后启动安装程序并退出。

## 上手

1. 打开“设置 → 对话模型”，选接口格式（OpenAI 兼容或 Anthropic），填服务地址，从下拉列表选模型，需要时填 API Key。保存即生效。
2. 在主界面输入框发一条消息。模型没配好时，消息留在队列里，输入框会提示缺什么。
3. 用语音：在“设置 → 本地引擎”下载引擎，或在“设置 → 语音输入 / 语音输出”填已有服务的地址。见[语音功能](doc/voice.md)。
4. 操作桌面：打开“设置 → 电脑操控 → 允许助手操作电脑”，按提示启用“桌面操控”子 agent。每个动作都要你点“允许”，提示在输入框上方，无人应答按拒绝处理。

模型也可以用环境变量配置，设置页的值优先：

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...       # 可选
export PARTNER_LLM_PROTOCOL=openai   # 可选：openai / anthropic
```

## 功能

| 功能 | 说明 | 文档 |
|---|---|---|
| 记忆 | 记下你提过的偏好，之后的回合可以调用。记忆页可以查看、在四个层级间移动、置顶和删除。 | [memory](doc/memory.md) |
| 子 agent | 需要操作桌面或读文件的请求由子 agent 在后台执行，完成后回报，失败会重试。 | [sub-agents](doc/sub-agents.md) |
| 桌面操控 | 列出窗口、点击控件、输入文字、启动程序。默认操作前需要批准，可以限定只操作哪些窗口。 | [desktop-control](doc/desktop-control.md) |
| 语音 | 唤醒词触发，按停顿断句，识别结果进队列，回复逐句朗读；可以设置朗读时不听麦克风。 | [voice](doc/voice.md) |
| 定时 | 支持 cron、固定间隔和指定时间。到点后内容作为普通消息进队列。 | [schedules](doc/schedules.md) |
| MCP | 接入 stdio MCP 服务器。工具调用默认需要批准，可以按服务器关闭。 | [mcp](doc/mcp.md) |
| Skills | 用 `SKILL.md` 描述一套做法，上传或在应用里新建；启用后加入模型提示。 | [skills](doc/skills.md) |

## 常见问题

**必须下载语音引擎吗？**
不用，只打字用不到。唤醒和识别引擎约 380 MB，纯 CPU；朗读引擎约 3 GB，需要显卡。也可以都不下，直接接已有的 ASR/TTS 服务。

**为什么朗读引擎分成几个文件？**
GitHub 单个 release 附件上限 2 GiB，朗读引擎约 3 GB。应用按顺序下载到同一目录并校验 SHA-256，断线后续传。

**数据存在哪里？**
默认在 `%LocalAppData%/Partner/workspace`，包括会话日志、队列、记忆库和设置。会话和队列可以从对应的 `.jsonl` 重建；`memory.db` 没有备份，删除后记忆无法恢复。

**会把我的数据传到别处吗？**
对话内容发给你配置的模型端点。“联网搜索”默认开启，搜索时查询语句会发给第三方搜索引擎，可以在“设置 → 联网搜索”关闭。应用还会访问 GitHub 检查更新和下载语音引擎。本地 API 只监听 `127.0.0.1`，请求必须带启动时生成的令牌。

**支持 macOS / Linux 吗？**
不支持。桌面操控和音频直接调用 Win32 API，换系统需要重写这两部分。

更多问题见[文档目录](doc/README.md)。

## 从源码构建

需要 .NET 8 SDK、Node 22 + npm（不要用 pnpm，esbuild 会装不上）、Rust（cargo 1.98+）。

```bash
npm --prefix ui install          # 首次
npx --prefix ui tauri dev        # 开发模式：启动前端和桌面壳，壳会拉起 API
bash scripts/build-shell.sh      # 打 NSIS 安装包
bash scripts/ci.sh               # 全量检查；SKIP_SHELL=1 跳过壳编译
```

调试方式和单独启动 API / 前端见[开发与调试](doc/development.md)，发版见[发布流程](doc/release.md)。

### 项目结构

| 路径 | 内容 |
|---|---|
| `src/Partner.Core` | 会话、队列、agent 回合、记忆、调度、MCP、Skills、治理 |
| `src/Partner.Desktop` | 窗口枚举、截图、输入注入、动作校验 |
| `src/Partner.Voice` | 麦克风输入、断句、ASR/TTS 客户端、播放 |
| `src/Partner.Api` | 本地 HTTP API、队列消费者、语音宿主、本地引擎管理 |
| `ui/` | Vue 3 前端，界面支持简中、繁中、英文 |
| `src-tauri/` | Tauri 2 桌面壳 |
| `engines/` | 本地语音引擎的服务脚本，运行时和模型不进 git |
| `scripts/` | CI、打包、引擎安装脚本 |

**技术栈**：C# / .NET 8 · Vue 3 · TypeScript · Tauri 2 · SQLite

## 参与贡献

欢迎提 issue，较大的改动请先开 issue 讨论。提 PR 前请在 Windows 上跑通 `bash scripts/ci.sh`；`master` 只接受 PR 合入。

## 许可

代码使用 [MIT](LICENSE) 许可。语音引擎的运行时、模型权重和音色锚点不在本仓库，各自遵循 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 列出的许可（Apache-2.0、FunASR MODEL_LICENSE 等），不受 MIT 覆盖。
