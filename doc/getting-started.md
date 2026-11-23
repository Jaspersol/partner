# 快速上手

**简体中文** | [繁體中文](./getting-started.zh-TW.md) | [English](./getting-started.en.md)

返回 [文档目录](./README.md)。

## 1. 准备环境

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 2004 及以上 |
| .NET | .NET 10 SDK |
| Node | Node 22 + npm。不要使用 pnpm：它默认会拦截依赖的构建脚本，导致 esbuild 无法安装 |
| Rust | cargo 1.98+，仅桌面壳需要 |
| WebView2 | Win10+ 自带 |
| 硬件 | 语音需要麦克风和扬声器；本地 TTS 需要显卡 |

## 2. 启动应用

```bash
npm --prefix ui install          # 首次运行
npx --prefix ui tauri dev        # 开发模式，Vue 修改会立即生效
```

桌面壳会自动选择空闲端口、启动 API，并把访问令牌注入界面。这一步不需要填写额外配置。

再次启动时，程序会聚焦已经存在的窗口，不会重复启动 API。如果本地服务无法启动或反复退出，连接页面会列出失败原因、退出码、失败次数和日志路径；查看日志后可以点击“重试”。默认日志位于 `%LOCALAPPDATA%\Partner\partner-api.log`。

## 3. 配置模型

打开“设置 → 对话模型”：

1. 选择接口格式：大多数本地服务使用 OpenAI 兼容格式；Claude 或实现 Anthropic Messages API 的端点使用 Anthropic。
2. 填写服务地址：OpenAI 兼容服务例如 `http://127.0.0.1:11434/v1`，Anthropic 服务例如 `https://api.anthropic.com/v1`。
3. 从下拉列表选择模型。列表由端点提供；如果获取失败，页面会显示原因。
4. 如有需要填写 API Key。该值只写入不读回，保存后界面只显示已经设置。

保存后立即生效。没有模型配置时，消息会留在队列中，输入区会提示尚未配置模型。Anthropic 格式目前不会请求扩展思考；界面中的“思考强度”只对 OpenAI 兼容端点有效。

保存设置或确认丢弃修改时，编辑控件会暂时锁定，操作结束后恢复。API Key 保存后不会再次显示。

也可以通过环境变量配置；设置页中的值优先：

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...       # 可选
export PARTNER_LLM_PROTOCOL=openai   # 可选，openai / anthropic
```

## 4. 开启语音（可选）

文字输入不依赖语音。需要语音时，请查看 [语音功能](./voice.md)：可以下载本地引擎，也可以填写现有的 ASR/TTS 服务地址。

## 5. 数据位置

默认工作区为 `%LocalAppData%/Partner/workspace`：

```
<workspace>/
  session/events.jsonl    会话日志
  queue/items.jsonl       输入队列
  memory/memory.db        记忆库（SQLite）
  memory/backups/         记忆库备份（每天维护前保存，保留最近 7 份）
  scheduler/              定时任务
  skills/                 技能
  media/                  附件
```

会话和队列都可以依据日志重建。记忆库至少每天备份到 `memory/backups/` 一次：启动时会补做一次，每日维护前也会检查，始终保留最近 7 份；备份完成后新增的记忆不会出现在该副本中。

删除会话历史时，如果日志文件替换失败，操作会报告错误；只要原文件能够重新打开，仍可继续写入。若无法恢复写入，存储会进入故障状态并拒绝后续写入。排除文件占用或权限问题后重新启动应用。

## 6. 打安装包

```bash
bash scripts/build-shell.sh      # SKIP_INSTALLER=1 只编译桌面壳
```

脚本按“发布 API → 构建前端 → 编译桌面壳 → 生成 NSIS 安装包”的顺序执行。顺序不能调换，否则安装包可能包含旧版 API。

默认安装流程通过 Tauri 的 `beforeBuildCommand` 构建一次前端；设置 `SKIP_INSTALLER=1` 时，脚本会先直接构建一次前端，再编译桌面壳。直接运行 `tauri build` 仍然会构建前端，但必须先自行发布 API。
