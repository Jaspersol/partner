# Todo — 桌面操控优化（按 2026 优化方案为基准）

基准：安全/精度工程**不动**。改动集中在**回合协议、感知层级、预算与审批的计量方式**三处。
顺序依赖：`P0 → P1a → {P2a, P2b}`；`P1b`、`P1c` 独立可并行；`P3` 全部按需。
每期做完统一收尾：smoke `Check(...)` + `Capabilities` 表 → `--update-baseline capabilities/manifest.baseline.json` → README 补相应小节（防文档漂移）。

**进度**：**P0、P1 全部完成**。全仓 `dotnet test` 973 绿 / smoke 164 项全绿（2 项 live skip）/ `GATE PASS`(62 能力) / README 与基线已同步。下一项：P2a（`scope:"desktop"` 全屏观察），其后 P2b。

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

## P2 — 补上「桌面」这一级观察

### 2a. `desktop_observe(scope:"desktop")`
- [ ] **P2a.1 全屏捕获**
  调用对象：`src/Partner.Desktop/Capture/GdiScreenCapture.cs` 加 `CaptureVirtualDesktopAsync`。
  做法：虚拟屏原点/尺寸直接调 `Input/VirtualDesktop.cs` 现成逻辑；单次 `BitBlt`（~60ms，不顶回 UIA 遍历的 10.2s）。
- [ ] **P2a.2 Desktop 实体**
  调用对象：`DesktopAutomation.Observe` + `Perception/DesktopEntities.cs`。
  做法：新增 `DesktopEntity`（role="desktop"，虚拟屏矩形）；UIA 树不走它；`ScreenshotMarks` 照常叠（窗口本身也编号，网格刻度即虚拟屏坐标——归一化已在注入层逐像素验过）。
- [ ] **P2a.3 截图入口**
  `desktop_screenshot` 接受 `entityId = desktop 实体` → 截全屏。
- [ ] **P2a.4 验收：smoke `desktop.desktop_scope`**
  全屏图能看到托盘某图的编号标记；用该 elementId 经 `desktop_act` 正常点击。

### 2b. px/py 就近元素兜底
- [ ] **P2b.1 升级逻辑**
  调用对象：`src/Partner.Desktop/Tools/DesktopTools.cs`（px/py 落点处理）。
  做法：服务端在同一次观察的元素列表里找半径 ε 内最近的可交互元素 —— 命中升级为 `elementId` 路径（得 pattern + 控件矩形校验的好处），未命中走坐标原路。ε = 8px（人眼实测偏差 ~5px）。
- [ ] **P2b.2 验收：smoke `desktop.px_snap_to_element`**
  px/py 偏 5px 仍点在按钮中心；偏 > ε 走坐标路径不升级。

---

## P3 — 接缝与外部条件（按需，不排期）

- [ ] **P3.1 CDP attach + 多标签**
  调用对象：`Perception/CdpRegistry.cs`（扫 loopback 常见端口 + `/json/version` UA 比对）、`Perception/CdpDomSource.cs`（枚举 `/json` 全部 targets，消除多标签随机性）。
  触发：用户实际抱怨「已开着的 Electron 应用动不了」。
- [ ] **P3.2 WebView2 自家壳走 CDP**
  壳以 `--remote-debugging-port` 启动 webview → 吃掉壳自身的 UIA 盲区（自家程序零安全争议）。
- [ ] **P3.3 grounding 模型副路**
  条件：真的接到本地部署的 OpenAI 兼容视觉端点（UI-TARS / Qwen2.5-VL / OmniParser 一类）。
  做法：截像 → 专门端点 → 点回服务端 px/py 同一条归一化。作为 `supportsImages=false` 却启用桌面时的回退。**现在不做**（第二客户端 = 探测/降级/账号全套新概念）。
- [ ] **P3.4 `feedback:"on-failure"` 挡（只在 NoChange 附图）**
  触发：P0 跑两周后看真实 token 消耗再定。

---

## 收尾清单（每期完成后勾）

- [ ] smoke 新检查写进 `src/Partner.Smoke/Program.cs` + `Capabilities` 表
- [ ] `dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json`
- [ ] README 相应小节补一行（当前 [[README]] 的「桌面操控」三节 + 「未做/未验证」表）
- [ ] 桌面测试串行约束仍然成立（坑 11/12）：跑 smoke 前停应用
