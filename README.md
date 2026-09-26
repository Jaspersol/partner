# Partner

本机 Agent 运行时。核心是一条**唯一逻辑会话**上的执行链：输入 → 队列 → Agent turn → 工具/记忆/治理 → 可编辑的会话日志，外加桌面控制、语音双向、游戏反射。

技术栈：**C# / .NET 8 运行时** + **Tauri 2 壳（Rust）** + **Vue 3 前端**。运行时用 C#（Win32 interop、音频、SQLite、MCP 都在这层），界面用 Web 技术（可 headless 测试），壳只负责起窗口和拉起 API。

设计文档在仓库外（随实现同步更新）：

| 文档 | 路径 |
|---|---|
| 目标架构方案（选型/缺口/分期/验收） | `D:/code/Clone/agent-harness-architecture-proposal.md` |
| 现状分析（被引用的源码核验） | `D:/code/Clone/agent-harness-desktop-game-implementation-analysis.md` |

---

## 快速开始

```bash
cd /d/code/Projects/partner
bash scripts/ci.sh
```

依次执行：.NET 编译 → 单元测试 → smoke → 能力门禁 → 前端类型检查/测试/构建 → 壳编译。任一步失败即中断，退出码非 0。

当前基线：**381 个 .NET 单测 + 17 个前端测试全绿 / 97 项 smoke 检查 / 49 项能力 `product-supported`**。

跳过最慢的壳编译：`SKIP_SHELL=1 bash scripts/ci.sh`。

### 环境要求

| 项 | 要求 |
|---|---|
| OS | **Windows**（`Partner.Smoke`/`Desktop`/`Voice` 是 `net8.0-windows10.0.19041.0`） |
| .NET | .NET 8 SDK（当前 8.0.423） |
| Node | Node 22 + npm（前端；**不用 pnpm**，见「坑」） |
| Rust | cargo 1.98+（仅壳需要；缺失时 CI 自动跳过该步） |
| WebView2 | Win10+ 自带（当前 153.x） |
| 硬件 | 真机语音需要麦克风/扬声器；桌面能力需要真实桌面会话 |

---

## 架构

```mermaid
graph LR
  subgraph shell["Tauri 壳（Rust，153 行）"]
    WIN["窗口 + 取空闲端口"]
    SIDE["拉起 partner-api<br/>从 stdout 读 token"]
    INJ["注入 window.__PARTNER_TOKEN__"]
  end
  subgraph web["Vue 3 前端（ui/）"]
    VIEWS["会话 / 队列 / 记忆 / 设置"]
  end
  subgraph runtime["C# 运行时（22.3k 行）"]
    API["Partner.Api<br/>Kestrel + Minimal API<br/>Bearer token + CORS 白名单"]
    CORE["Core · Desktop · Voice · Game"]
    API --- CORE
  end
  SIDE -->|spawn + 读 token| API
  INJ --> VIEWS
  VIEWS -->|HTTP + Bearer| API
```

**唯一接缝是 HTTP + Bearer token**：壳不知道任何业务逻辑，前端不引用任何 .NET 程序集，运行时不知道 UI 用什么技术。

### 目录结构

| 路径 | 平台 | 职责 |
|---|---|---|
| `src/Partner.Core` | net8.0 | 会话日志、输入队列、调度、Agent turn、MCP、记忆、治理、契约。**全部逻辑所在** |
| `src/Partner.Desktop` | net8.0-windows | 窗口枚举、GDI 抓帧、输入编组、动作后校验、租约门控 |
| `src/Partner.Voice` | net8.0-windows | 麦克风输入（VAD/分句/流式）、TTS 输出、播放期停听 |
| `src/Partner.Game` | net8.0 | 游戏通用层：反射引擎（槽位锁）、六域上下文、感知去抖分级、进程适配器 |
| `src/Partner.Api` | net8.0 | **UI 的唯一数据源**。Kestrel + Minimal API |
| `src/Partner.Smoke` | net8.0-windows | 端到端 smoke + 能力清单生成 + 门禁 |
| `ui/` | — | Vue 3 + Vite + TS 前端（四视图 + 契约类型 + 测试） |
| `src-tauri/` | Rust | 桌面壳：窗口、sidecar 生命周期、token 注入 |
| `tests/Partner.GameTestServer` | net8.0 | 合成网格游戏，作为**真实子进程**供游戏层闭环验证 |
| `tests/Partner.McpTestServer` | net8.0 | MCP stdio 夹具 |

### 运行期产物

工作区（默认 `%LocalAppData%/Partner/workspace`，壳与手工启动的 API 共用同一个）：

```
<workspace>/
  session/events.jsonl    追加式会话日志（唯一真相，编辑是追加 Edit 事件）
  queue/items.jsonl       队列日志（重启即重放）
  memory/memory.db        SQLite + FTS5 记忆库
  settings/voice.json     语音设置（首次保存时创建）
```

删除工作区即回到初始状态——会话与队列从日志重建，**记忆库是权威存储，删除即丢失**。

---

## 运行

### 完整应用（壳 + API）

```bash
# 开发模式（热更新：改 Vue 立刻生效）
npm --prefix ui install                 # 首次
npx --prefix ui tauri dev               # 起 vite + 壳，壳自动拉起 API

# 生产打包（会先 dotnet publish API，再打 NSIS 安装包）
dotnet publish src/Partner.Api -c Release -r win-x64 --self-contained true \
  -o src/Partner.Api/bin/Release/net8.0/win-x64/publish
npm --prefix ui run build
npx --prefix ui tauri build
```

壳自己挑空闲端口、自己起 `partner-api`、自己把 token 注入 webview —— **没有需要手填的配置**。

### 只起 API

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

未提供 token 时会生成一个并打印到 stdout（仅此一次）：

```
partner-api listening on http://127.0.0.1:5280
token: <base64url>
```

所有端点都要求 `Authorization: Bearer <token>`，**包括 `/api/health`**：

```bash
TOKEN=<上一步打印的>
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/session
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/queue
curl -X POST -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/projections/rebuild
```

### 只起前端（浏览器里调）

```bash
npm --prefix ui run dev     # http://localhost:5173
```

浏览器控制台里先放 token（前端不接受 URL 参数，避免 token 进历史记录）：

```js
localStorage.setItem('partner.token', '<token>')
localStorage.setItem('partner.port', '5280')
```

CORS 白名单已包含 `http://localhost:5173`。

---

## 调试

### 分层命令

```bash
dotnet build                                                     # .NET 编译
dotnet test                                                      # 全部 .NET 单测
dotnet test --filter "FullyQualifiedName~ReflexEngine"            # 单个测试类
dotnet run --project src/Partner.Smoke                            # 只跑 smoke（须在仓库根）

npm --prefix ui run typecheck                                     # 前端类型检查
npm --prefix ui run test                                          # 前端测试（含对真实 exe 的契约测试）
npm --prefix ui run test -- tests/views.test.ts                   # 单个前端测试文件
npm --prefix ui run build                                         # 前端构建

cargo build --manifest-path src-tauri/Cargo.toml                  # 只编译壳
```

### 测试分层

| 层 | 位置 | 覆盖什么 |
|---|---|---|
| .NET 单测（381） | `tests/` | 各层逻辑：会话/队列/调度/记忆/治理/MCP/桌面/语音/游戏/API（含 API 进程生命周期） |
| smoke（97 检查） | `src/Partner.Smoke` | 端到端：真实进程、真实窗口、真实 HTTP、10 万条检索 P99 |
| 前端契约测试（9） | `ui/tests/api.contract.test.ts` | **拉起真实 `partner-api.exe`**，验证 TS 类型与服务端 JSON 不漂移、鉴权生效 |
| 前端视图测试（8） | `ui/tests/views.test.ts` | 四个视图的渲染与交互（挂载真实组件 + 桩 API） |
| **壳的端到端（9）** | `scripts/e2e-shell.mjs` | 启动**发布版壳**，经 CDP 驱动真实 webview：选中→编辑→保存、切页签、验鉴权、验 sidecar 回收。**不在 CI**（需要桌面会话），按需手动跑 |
| 能力门禁（49） | `capabilities/manifest.baseline.json` | 能力消失或降级即失败 |

### 落盘点

| 现象 | 去哪看 |
|---|---|
| smoke 某检查失败 | 工作区**保留**在开头打印的 `workspace: <path>` |
| API 起不来 | 壳会把它 stderr 的第一行带进 panic 信息；手工跑则直接看终端 |
| 前端请求失败 | 浏览器 DevTools（开发模式）；401 表示 token 不对/过期 |
| 壳里的界面行为 | `node scripts/e2e-shell.mjs` —— WebView2 不把 DOM 暴露给 UIA，CDP 是唯一入口 |
| 壳 panic | 终端输出；`RUST_BACKTRACE=1` 拿完整栈 |

### 各子系统怎么调

| 目标 | 手段 | 前置 |
|---|---|---|
| Core（会话/队列/调度/记忆/治理/MCP） | `dotnet test --filter` 或往 smoke 加检查 | 无 |
| Desktop | smoke 的 desktop 段 | 真实桌面会话 |
| Voice | smoke 走合成路径；真机验证跑壳 | 麦克风/扬声器 |
| Game | smoke 自动拉起 `Partner.GameTestServer`；也可单独跑它喂 JSON 协议 | 无 |
| Api | curl（记得带 token） | 无 |
| 前端 | `npm --prefix ui run dev` + 浏览器 | token 放进 localStorage |

---

## 能力门禁

规则：**能力等级由实跑的 smoke 检查推导，配置永远不能决定**。门禁与基线比对：

- 能力**消失或降级** → `GATE FAIL`，退出码 1
- 能力**新增** → 提示 `update the baseline to lock it in`（不失败）

加一条 smoke 检查：

1. 在 `src/Partner.Smoke/Program.cs` 的 `RunAsync` 对应段加 `Check("模块: 行为", 条件, 细节)`
2. 把检查名加进同文件 `Capabilities` 表对应模块
3. `dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json`
4. 此后 CI 自动守住它

---

## 模块 → 源码

| 模块 | 主要位置 |
|---|---|
| M1 会话 | `Core/Sessions/`（`JsonlSessionStore`、`SessionEvent`、`SessionView`）、`Core/Storage/` |
| M2 队列 | `Core/Queue/`（`JsonlInputQueue`、`InputItem`） |
| M3 Agent | `Core/Agent/`（`AgentTurnRunner`、`Hooks/`、`Llm/`、`Tools/`、`Governance/`、`Mcp/`、`Skills/`） |
| M4 调度 | `Core/Scheduler/`（`JsonlScheduler`、`CronExpression`） |
| M5 记忆 | `Core/Memory/`（`MemoryStore`、`MemorySchema`、`MemoryScorer`、`Embeddings`、`TokenCounter`） |
| M6 治理 | `Core/Agent/Governance/`、`Core/Agent/Tools/`（`ToolGovernance`、`InMemoryToolRegistry`） |
| M7 MCP | `Core/Agent/Mcp/` |
| M8 Skills | `Core/Agent/Skills/` |
| M9 桌面 | `Desktop/`（`Windows/`、`Capture/`、`Input/`、`Interop/`、`Perception/`、`DesktopAutomation`） |
| M10/M11 语音 | `Voice/`（`Audio/`、`Asr/`、`Perception/`、`Tts/`、`Playback/`、`Settings/`） |
| M12 游戏 | `Game/`（`Reflex/`、`Perception/`、`Adapter/`） |
| M13 UI | `Api/ApiHost.cs`、`Api/Auth/TokenAuth.cs`、`Api/Endpoints/ViewEndpoints.cs`、`ui/src/`、`src-tauri/src/lib.rs` |

---

## 已实现能力（49）

括号内是支撑该能力的 smoke 检查数。

| 模块 | 能力 |
|---|---|
| M1 会话 | `session.append_only_log`(6)、`session.editable_projection`(5) |
| M2 队列 | `queue.single_fifo`(5)、`queue.idempotent_restart`(3)、`queue.crash_recovery`(2) |
| M3 Agent | `agent.turn_state_machine`(5)、`agent.tool_loop`(1)、`agent.hook_phases`(1) |
| M4 调度 | `scheduler.durable_delivery`(3)、`scheduler.receipt_dedup`(1)、`scheduler.failure_retention`(2)、`scheduler.schedule_to_turn`(3) |
| M5 记忆 | `memory.recall_pipeline`(2)、`memory.offline_degradation`(1)、`memory.privacy_delete`(1)、`memory.promotion`(1)、`memory.injection_budget`(1)、`memory.scale_p99`(1) |
| M6 治理 | `governance.policy_decisions`(2)、`governance.fail_closed`(1)、`governance.target_param_rules`(1)、`governance.enforced_in_turn`(1) |
| M7 MCP | `mcp.stdio_lifecycle`(2)、`mcp.tool_bridge`(1)、`mcp.governed_turn`(1) |
| M8 Skills | `skills.workspace_scan`(1)、`skills.injection`(2) |
| M9 桌面 | `desktop.window_enumeration`(1)、`desktop.gdi_capture`(1)、`desktop.post_action_verification`(1)、`desktop.lease_gating`(3)、`desktop.input_marshalling`(1) |
| M10/M11 语音 | `voice.utterance_to_queue`(2)、`voice.asr_contract`(1)、`voice.threshold_guard`(1)、`voice.playback_gate`(4)、`voice.failure_visibility`(2)、`voice.output_ordering`(1)、`voice.output_switch`(1) |
| M12 游戏 | `game.process_adapter`(3)、`game.reflex_loop`(2)、`game.sensor_debounce`(1)、`game.latency_monitor`(1) |
| M13 UI | `ui.auth`(2)、`ui.session_view`(1)、`ui.memory_view`(2)、`ui.queue_view`(1)、`ui.settings`(1)、`ui.projection_rebuild`(1) |

---

## 必须知道的坑

1. **API 的 token 是 fail-closed 的**：中间件保护**每一个**端点（含 `/api/health`），新增端点自动受保护。token 走环境变量 `PARTNER_API_TOKEN`，**不走命令行**（argv 全机可读）。
2. **CORS 顺序**：`UseCors()` 必须在 `UseTokenAuth()` **之前**——预检请求不带凭据，否则会被 401 挡掉。白名单是枚举的 Tauri 来源，不是 `*`。
3. **前端用 npm，不要用 pnpm**：pnpm 11 默认拦截依赖的构建脚本，esbuild 装不上（`ERR_PNPM_IGNORED_BUILDS`），vite 直接不可用。
4. **壳的资源必须是整目录**：`partner-api` 是 .NET apphost，需要同目录的 `.dll`；Tauri 的 `resources` 若配成单文件，dev 下会得到"找不到 partner-api.dll"。
5. **CI 只能在 Windows 跑**（见「环境要求」）。
6. **改能力要更新基线**，否则下次门禁提示 `update the baseline to lock it in`。
7. **改完 API 必须重新 `dotnet publish` 再打包**：壳打包的是 publish 快照，不是 Debug 输出。dev 下壳优先用 Debug 输出（改完 `dotnet build` 即生效），release 下只用打包目录。
8. **`cargo build --release` 需要 `custom-protocol` feature**（已在 `Cargo.toml` 里设为 default）。少了它，release 二进制仍指向 `devUrl`，窗口里是 `ERR_CONNECTION_REFUSED`。`tauri dev` 用 `--no-default-features` 走 dev server，这是设计如此。
9. **本仓库尚未纳入版本控制**（没有 `.git`）。改动没有回滚点，建议先 `git init` 并提交当前状态。

---

## 未做 / 未验证

诚实边界，避免误判完成度：

| 项 | 状态 |
|---|---|
| M9 L2（UIA 语义树 / OCR） | 未做。当前桌面感知只到窗口枚举 + GDI 抓帧 |
| 游戏商业目标 | 未接入。当前目标是自研合成网格游戏，用于证明闭环而非证明某个具体游戏 |
| 游戏浏览器/CDP 通道 | 未验证（环境受阻）。进程边界适配器已跑通并实测延迟 |
| 反射层真实节奏 | 未实测。单测与 smoke 都是受控节拍，真实游戏里的信号节奏仍需一次实测 |
| 壳的交互验证 | ✅ 已覆盖：`node scripts/e2e-shell.mjs`（9 项，CDP 驱动真实 webview）。**不在 CI**——需要桌面会话，且 CI 无法断言窗口 |
| WebView2 的 UI Automation | UIA 看不到 DOM（实测只暴露 16 个元素）；CDP 可以，e2e 就是走 CDP |
| 生产打包 | ✅ 已跑：`Partner_0.1.0_x64-setup.exe` **34.7 MB**（含 101 MB 自包含 API，NSIS 压缩）。**未在干净机器上安装验证** |
| 记忆页签空态 | API 按设计没有「新建记忆」端点（写入来自 Agent），因此该页签在空工作区是空列表 |
| Phase 5c 帧级闭环 | 未做 |
