# Todo — 桌面操控优化（按 2026 优化方案为基准）

基准：安全/精度工程**不动**。改动集中在**回合协议、感知层级、预算与审批的计量方式**三处。
顺序依赖：`P0 → P1a → {P2a, P2b}`；`P1b`、`P1c` 独立可并行；`P3` 全部按需。
每期做完统一收尾：smoke `Check(...)` + `Capabilities` 表 → `--update-baseline capabilities/manifest.baseline.json` → README 补相应小节（防文档漂移）。

**进度**：**P0、P1、P2a、P2b 全部完成；P3.1 的多标签半已提级修复，其余按需/定为不做**。全仓 `dotnet test` 975+ 绿 / smoke 166 项全绿（2 项 live skip + 15 项单测 live skip）/ `GATE PASS`(64 能力) / README 与基线已同步。剩余：P3（按需：CDP attach、WebView2 CDP、grounding 副路、on-failure 挡）——均为外部条件触发，未排期。

**不做的（防止回流）**：不改 `InputInjector` / 坐标归一化（`ceil` 公式）/ 护栏四步序 / 租约身份模型 / `PostActionVerifier` 控件矩形判定（已测封印资产）；不做 OCR；不做沙箱/第二桌面；不把桌面工具 MCP 化。

---

## P0 — `desktop_act` 返回带新状态（最优先，其余改动的杠杆）— **已完成**

- [x] **P0.1 act 路径附图**
  实现：`DesktopScreenshotTool.RenderAfterActionAsync`（与显式截图工具同一条管线：捕获 → 缩放 → `ScreenshotMarks` 叠标注 → media 落盘 → data URI）；`DesktopTools.cs` act 结果构造处调用后经 `ToolResult.Images` 上线（通道 `AgentTurnRunner.cs:339` 不变）。
  附图标注用 **fresh 观察的那批 elementId**（act 重校验时刚跑的同一次遍历），保证图上的标记就是模型下一步能按名寻址的那批。
- [x] **P0.2 参数与例外**
  schema 加 `feedback: "auto" | "off"`，默认 `auto`。判定集中在 `DesktopActTool.WantsPicture(kind, feedback, supportsImages, guardPassed)`：拒绝、`focus/movePointer/scrollIntoView`、`off`、纯文本模型 → 不附。
  另一条实测补上：`target.IsMinimized || IsIconic(handle)` 任一为真不加图（最小化窗口没有像素可展示）。
- [x] **P0.3 返回文字补一行**
  `已附截图 <mediaId>（W×H，动作后画面 @ 屏幕 X,Y W×H；px/py 可直接对它用）…`。
- [x] **P0.4 验收**
  smoke：`desktop.act_returns_state`(2) —— 决策矩阵 + 附图对 px/py 可映射、按 media id 记录（重启 `manifest.baseline.json` 更新，门禁 `GATE PASS`，59 项能力）。
  单测：`DesktopActFeedbackTests`（10 条：决策矩阵 6 + 渲染/降级/最小化 4，stub capture，不碰真实桌面）。
  live（默认 skip）：`DesktopLiveInteractionTests.An_injected_click_reaches_a_real_button` 已扩展为断言真点击后结果带图。
  （已知边界：渲染失败静默降级为无图，文字结果不变 —— 这是刻意的，不是漏。）

---

## P1 — 预算与审批的计量方式（成本最低、收益第二）— **P1a / P1b 已完成，P1c 待做**

### 1a. 步预算按工具族拆 — **已完成**
- [x] **P1a.1 判停改按族**（实现：`AgentTurnOptions.ToolStepBudgets`，前缀 `desktop` / `mcp_`；`StepsAllowedFor(registeredToolNames)` 按**注册表里有什么**取最大那支 —— 在动作发生前就知道决算，不会死在第一次 desktop 调用之前。默认内置 `desktop:32 / mcp_:32`，普通仍 8）
- [x] **P1a.2 选项**（`AgentTurnRunnerTests` 新 3 条：desktop 工具集可走 13 步、纯 chat 工具集仍 8 步即停、最大匹配与上限语义；`MaxSteps` 仍是所有族预算的地板也是下限）
- [x] **P1a.3 验收：smoke `desktop.step_budget_by_family`**（真实注册表形状 → desktop>8 且 plain==8）

### 1b. 审批从「逐动作」变「任务租约」
### 1b. 审批从「逐动作」变「任务租约」— **已完成**（实现不同于原计划：不靠 UI 调租约端点，改在**批准应答路径**上自动记账）

- [x] **P1b.1 治理判定**（分享：`Api/Settings/TaskLeaseStore.cs` —— 授予/消费/快照；身份是**窗口句柄**，与观察租约同一语义；`Tools/LeaseGrantingApprovalHandler.cs` 包住 `QueuedApprovalHandler`，desktop_act 被人真批了才自动授后续 10 步；`DesktopGovernance.Decide` 里 desktop_act 先查租约再查挡位。**危险类由构造隔离**：只有 desktop_act 进租约分支）
- [x] **P1b.2 横幅文案**（`ApprovalBanner.vue` 的提示改为「允许将授权目标窗口继续 N 步（危险类仍逐次询问）」；`stepsPerGrant` 是**服务端给出**的数字，随 `GET /api/desktop` 下来，前端不抄常量）
- [x] **P1b.3 验收：smoke `desktop.task_lease_approval`(3)（真实 consumer + 真实审批队列：批准 → `GET /api/desktop` 可见租约；同窗口第二次 act 不进 pending 且租约从 10 掉到 9；同一租约内 desktop_exec 仍弹且拒绝后租约不变）**

### 1c. `desktop_wait` 原语 — **已完成**
- [x] **P1c.1 StabilitySensor**
  新建：`src/Partner.Desktop/Perception/StabilitySensor.cs`。
  实现：**不**改旧 `PostActionVerifier`（校验策略与轮询参数捆绑，重建只会引入回归风险），独立实现「连续两帧哈希相同才算稳」；verifier 不动、对其的现有测试全绿。`ClampTimeout` 硬顶 5s。
- [x] **P1c.2 新工具**
  新建：`src/Partner.Desktop/Tools/DesktopWaitTool.cs`。
  schema：`{kind:"element"|"window"|"stillFrame", entityId?, elementId?, titleContains?, timeoutMs?, stableRounds?}`。
  超时硬顶 5s；**超时报「未达成」并给出当前状态**，不是挂着烧步数。只读类 → 永不审批。
- [x] **P1c.3 名单登记**
  `Tools/DesktopToolCatalog.cs`：`desktop_wait` 进 `All` 与只读集；`ConsumerHost` 注册；`/api/consumer` 工具表同步。
- [x] **P1c.4 验收**
  单测 8 条（`DesktopWaitToolTests`）：封顶、两帧才算稳、永不停则超时、已有控件一次命中且附新观察、窗口不存在报「现在开着」、坏形参拒绝、只读分类、未知窗口/kind 拒绝。
  smoke `desktop.wait_primitive`(1)：真实窗口的已有控件一次命中且带新 id。

---

## P2 — 补上「桌面」这一级观察 — **2a / 2b 已完成**

### 2a. 全屏观察（实现放在 `desktop_screenshot{scope:"desktop"}`，不动 observe 的实体列表）
- [x] **P2a.1 全屏捕获**
  调用对象：`src/Partner.Desktop/Capture/GdiScreenCapture.cs` 加 `CaptureVirtualDesktopAsync`。
  做法：虚拟屏原点/尺寸直接调 `Input/VirtualDesktop.cs` 现成逻辑；单次 `BitBlt`（~60ms，不顶回 UIA 遍历的 10.2s）。
- [x] **P2a.2 窗口编号标记**（`ScreenshotMarks.WindowMarks`：纯几何 —— 标记=窗口 entityId + 中心点；最小化/越界窗口与元素标记同规则跳过。没有为桌面造 UiEntity：桌面实体进列表会污染裸坐标命中（ResolveByPoint 的覆盖歧义）与窗口筛选，全屏路径不需要它）
- [x] **P2a.3 截图入口**（`desktop_screenshot` 加 `scope:"desktop"`；无观察前提（第一次看不能以先看过为前提）；结果 `ShownPicture(entityId="desktop")`；act 端 px/py 全屏图映射为屏幕坐标后走裸坐标路径——命中测试/遮挡/z 序全过，非桌面图仍然要求窗口身份）
- [x] **P2a.4 验收**（单测 5 条：几何/最小化跳过/无观察可出图/标记 id 可被 act 找到/px/py 落点±1px；smoke 2 条：无观察全景+media 记账、px/py 裸坐标解析落点）

### 2b. px/py 就近元素兜底 — **已完成**
- [x] **P2b.1 升级逻辑**
  调用对象：`src/Partner.Desktop/Tools/DesktopTools.cs`（px/py 落点处理）。
  做法：服务端在同一次观察的元素列表里找半径 ε 内最近的可交互元素 —— 命中升级为 `elementId` 路径（得 pattern + 控件矩形校验的好处），未命中走坐标原路。ε = 8px（人眼实测偏差 ~5px）。
- [x] **P2b.2 验收**（单测 6 条：邻域内升级、半径外不升级、平局取 id、Document/空白矩形/他窗口/offscreen 不升级；live 门控 1 条实包；smoke 1 条：key 拒绝路径解析出控件名 + 目标写进 guard 拒绝文本）

---

## P3 — 接缝与外部条件（按需，不排期）

- [x] **P3.1 的「多标签」半 —— 已升级为全线修复（2026 本轮）**：这不是等抱怨的功能缺口而是正确性 bug——两个标签共享一个调试端口时，`/json/list` 的第一个 tab 会同时供两个窗口读写（第二窗口按自己的客户区原点换算第一个 tab 的 DOM，点击也落在第一个 tab 里）。修法：`CdpDomSource.ConnectAsync(port, windowTitle)` 按窗口标题与页面标题双向包含匹配选页（Chromium 窗口标题 = 页 title + 后缀；Electron 可能相反）；连接缓存从「每端口一条」改为「每 (端口， 页) 一条」（targets 各自 webSocketDebuggerUrl，每 target 一个客户端的限制不变）；匹配不上仍回退第一个（旧行为）。单测 7 条（`CdpDomTests` 匹配规则 4 +  Fake connect 选页 2 + 旧 viewport 1 已有）；未验证多标签在线上真吃到匹配（live 是单页 Edge）。
- [ ] **P3.1 的「attach」半 —— 维持按需**
  扫 loopback 常见端口找已开的调试目标；价值低（大多数真实用户的浏览器未带调试端口，attach 无从得手；仅开发者自带 flag 的 Electron 应用受益）。触发不变：用户实际抱怨且场景明确时。
- [ ] **P3.2 WebView2 自家壳走 CDP —— 定为不做**：自家壳的会话/设置操作有更正确的路（API + 治理），让 agent 用桌面工具去点自家 UI 是最差路径（且 agent 自己的窗口正好挡在用户眼前，hit-test 抬前台的冲突就在那）。写明设计立场：模型对自家应用的修改走 HTTP API，不走 desktop 工具。
- [ ] **P3.3 grounding 模型副路（已收紧为“纯文本模型专用”）**
  结论：**接入了视觉模型（`supportsImages=true`）后不做**——坐标读数的精度由已有三层机制承担
  （网格/标记标准化读数、zoomAt 两段瞄、`SnapToElement` 8px 邻域吸附），剩余优化的顺序是：
  ① 先用真实任务遥测量（吸附命中率在 act 结果文本里免费可见）→
  ② 偏差大再调护栏（P2b 半径按控件尺寸归一化、出图分辨率提高、zoomAt 参数化）→
  ③ 仍不够才谈第二通道。
  仅剩触发条件：**配置纯文本模型却仍启用桌面**（UIA 盲区变盲）、且有一个本地部署的 OpenAI 兼容轻量端点——此时它才从「第二模型」降级为「能力回退」。做法不变：截像 → 专端点 → 点回服务端 px/py 同一条归一化。代价不变：第二客户端 = 探测/降级/账号全套新概念。
- [ ] **P3.4 `feedback:"on-failure"` 挡（只在 NoChange 附图）**
  触发：P0 跑两周后看真实 token 消耗再定。

---

## 收尾清单（每期完成后勾）

- [ ] smoke 新检查写进 `src/Partner.Smoke/Program.cs` + `Capabilities` 表
- [ ] `dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json`
- [ ] README 相应小节补一行（当前 [[README]] 的「桌面操控」三节 + 「未做/未验证」表）
- [ ] 桌面测试串行约束仍然成立（坑 11/12）：跑 smoke 前停应用
