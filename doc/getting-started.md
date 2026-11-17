# 快速上手

**简体中文** | [繁體中文](./getting-started.zh-TW.md) | [English](./getting-started.en.md)

返回 [文档目录](./README.md)。

## 1. 准备环境

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 2004 及以上 |
| .NET | .NET 10 SDK |
| Node | Node 22 + npm。不要用 pnpm：它默认拦截依赖的构建脚本，esbuild 装不上 |
| Rust | cargo 1.98+，只有桌面壳需要 |
| WebView2 | Win10+ 自带 |
| 硬件 | 语音需要麦克风和扬声器；本地 TTS 需要显卡 |

## 2. 启动

```bash
npm --prefix ui install          # 首次
npx --prefix ui tauri dev        # 开发模式，改 Vue 立即生效
```

桌面壳会自己挑空闲端口、拉起 API、把访问令牌注入界面。这一步不需要填任何配置。

重复启动会聚焦已有窗口，不会再启动一个 API。若本地服务无法启动或持续退出，连接页面显示失败原因、退出码、失败次数和日志路径；检查日志后可按“重试”。默认日志位于 `%LOCALAPPDATA%\Partner\partner-api.log`。

## 3. 配模型

打开“设置 → 对话模型”：

1. 先选接口格式：大多数本地服务选 OpenAI 兼容；Claude 或兼容 Anthropic Messages 的端点选 Anthropic。
2. 填服务地址：OpenAI 兼容例如 `http://127.0.0.1:11434/v1`，Anthropic 例如 `https://api.anthropic.com/v1`。
3. 从下拉里选模型。列表是从端点拉取的，拉不到会写明原因。
4. 需要时填 API Key。它只写不读，保存后界面只显示“已设置”。

保存后立即生效。没配模型时，消息会停在队列里，输入区写明“未配置模型”。Anthropic 格式目前不请求扩展思考，界面里的“思考强度”只对 OpenAI 兼容端点生效。

保存或确认丢弃期间，设置编辑控件暂时锁定；操作结束后可继续编辑。API Key 保存后不会回显。

也可以用环境变量，设置页的值优先：

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...   # 可选
export PARTNER_LLM_PROTOCOL=openai   # 可选，openai / anthropic
```

## 4. 打开语音（可选）

文字输入不依赖语音。要用语音，见 [语音功能](./voice.md)：可以下载本地引擎，也可以填已有的 ASR/TTS 服务地址。

## 5. 数据位置

默认工作区是 `%LocalAppData%/Partner/workspace`：

```
<workspace>/
  session/events.jsonl    会话日志
  queue/items.jsonl       输入队列
  memory/memory.db        记忆库（SQLite）
  memory/backups/         记忆库备份（每天维护前，保留最近 7 份）
  scheduler/              定时任务
  skills/                 技能
  media/                  附件
```

会话和队列可以从日志重建。记忆库至少每天备份一次到 `memory/backups/`（启动时补一次，每日维护前也检查），保留最近 7 份；备份之后写入的记忆不在副本里。

清除会话历史时，若日志文件替换失败，操作会报错；原文件能重新打开时仍可继续写入。若无法恢复写入，存储会进入故障状态并拒绝后续写入；排除文件占用或权限问题后重启应用。

## 6. 打安装包

```bash
bash scripts/build-shell.sh      # SKIP_INSTALLER=1 只编译壳
```

脚本按“发布 API → 构建前端 → 编译壳 → 打 NSIS 包”的顺序执行。顺序不能换，否则安装包里会是旧的 API。
默认安装包由 Tauri 的 `beforeBuildCommand` 构建一次前端；`SKIP_INSTALLER=1` 由脚本直接构建一次前端后编译壳。直接调用 `tauri build` 仍会构建前端，但需先自行发布 API。
