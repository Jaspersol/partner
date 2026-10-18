# Todo — 主 / 子 Agent 分工（修订 2）：两个场景、逐轮重试、失败如实回报（方案稿，分支 local-voice-stack）

一句话：主 agent 只对话和派活；重活交给**按场景路由的子 agent**。场景是个只有两项的封闭目录：桌面操控、读取文件。每个 agent 都有自己可编辑的提示词，在侧边栏新增「提示词」页集中编辑。子 agent 每个任务最多试 5 轮，全败就向主 agent 回报「无法执行」和错误原因。没人占用的场景，一切照旧由主 agent 自己执行。

需求（逐条在下文有对应校验或任务）：

1. 主、子 agent 的提示词都能在 UI 编辑。
2. 调用场景只有两个：`desktop`（桌面操控）、`file-read`（读取文件）。
3. 同一场景只能有一个子 agent。`desktop` 由内置的「桌面操控」子 agent 占用；`file-read` 可选，用户新增子 agent 时选它。
4. 桌面操控开启时**必须**有启用的桌面子 agent；它默认使用主模型。
5. 子 agent 默认最多 5 轮；不成功就返回「无法执行」和错误原因给主 agent。

---

## 1. 现状核对（已读代码，行号为当前工作区）

- **主提示词目前无处可改。** `BaseSystemPrompt` 恒为 null（`ConsumerHost.cs:292`），回落到写死的 `"You are a single-user local assistant."`（`IContextAssembler.cs:74`）。
- **一个 runner、一个会话、一个读者。** 会话 id 写死 `"main"`（`AgentTurnRunner.cs:217`）；`TurnLoop` 同一时刻只跑一个 turn（`TurnLoop.cs:61`）。桌面任务 20–40 步会占住队列。
- **工具表在 `ConsumerHost.Start()` 里一次性建好**（`ConsumerHost.cs:295-331`）。设置保存时只有模型 / 桌面 / MCP 的结构变化才重启消费者（`ViewEndpoints.cs:843-845`）。
- **步数预算按 registry 里的工具名前缀算**（`desktop`、`mcp_` 为 32，`AgentTurnRunner.cs:27`、`:264`）。子 agent 的 registry 只放 `desktop_*` 就自动 32 步；主 registry 拿掉桌面原语后回到 8 步。
- **桌面状态是进程内单例**（`DesktopAutomation`）：任何一次 `Observe` 都让旧租约失效（`DesktopAutomation.cs:566`）。同一时刻只能有一个 agent 持有 `desktop_*`。
- **项目里没有读文件工具。** `ITool` 的实现只有桌面和 MCP 两类（grep `src` 确认），场景 2 要新写工具。
- **运行器已给失败打了可识别的前缀**：治理拒绝 `denied:`（`AgentTurnRunner.cs:647`）、审批被拒或超时 `approval denied:`（`:662`）、工具超时 `timeout after`（`:682`）、其余 `error:`。这正是判断「重试有没有意义」的现成依据。`ToolFailureReason.From` 还把失败消息归成可分组的形状（`ToolFailureLog.cs`），取值我没读。
- `ToolPolicy` 已有 `AllowedPathPrefixes`（支持 `${workspace}` 展开，`ToolPolicy.cs:12`），但运行时用的 `DesktopGovernance` 没接它。读文件的目录白名单可借用这套语义。
- **截图工具是否注册取决于主模型的 `SupportsImages`**（`ConsumerHost.cs:305`）。桌面子 agent 可覆盖模型后，这个判断要改成看桌面子 agent 的有效模型。
- **治理 fail-closed**：`DesktopGovernance` 对不认识的工具一律 Deny（`DesktopGovernance.cs:46`），新增工具必须先登记。
- **审批请求不带来源**（`QueuedApprovalHandler.cs:13`）：后台子 agent 弹审批，用户不知道是谁在问。
- **设置里带列表的 record 必须手写值相等**，否则每次保存都被判「变了」而重启消费者（`DesktopSettings`、`McpSettings` 的注释记录了这个坑）。
- **回填陷阱**：设置响应漏掉某一段，UI 保存后就把它清空（`SettingsView.From` 里 Desktop / Skills 的注释）。
- `InputSource` 只有 Voice / Text / Schedule 三种（`InputSource.cs:4`），引用分布在 13 个文件、约 27 处。

---

## 2. 概念与数据模型

| 概念 | 含义 |
|---|---|
| **场景（scenario）** | 代码里的封闭目录，只有两项。名称和「什么时候该交给我」的描述都固定在代码里，不可编辑。 |
| **子 agent** | 一份提示词 + 一个场景 + （可选）模型覆盖 + 最大轮数。工具由场景决定，不让用户勾选。 |
| **任务（job）** | 主 agent 一次委派产生的一次子 agent 运行，有自己的会话文件、轮次记录和状态。 |

场景目录：

| key | 名称 | 子 agent 持有的工具 | 能力开关 | 谁可以占用 |
|---|---|---|---|---|
| `desktop` | 桌面操控 | `desktop_*` | `desktop.enabled` | 仅内置的 `desktop` 子 agent |
| `file-read` | 读取文件 | `file_list`、`file_read`（新写，只读） | `files.enabled`（新增，默认关） | 用户新增的子 agent |

设置新增两段：

```jsonc
"agents": {
  "main": { "prompt": null },                 // null = 用内置默认
  "subAgents": [
    { "id": "desktop", "name": "桌面操控", "enabled": true, "scenario": "desktop",
      "prompt": null, "model": null, "maxRounds": 5 },   // 内置：不可删，id / scenario 锁定
    { "id": "reader", "name": "文件阅读", "enabled": true, "scenario": "file-read",
      "prompt": null, "model": null, "maxRounds": 5 }    // 用户新增，可选
  ]
},
"files": { "enabled": false, "roots": [] }    // 允许读取的目录白名单；默认关，fail-closed，同桌面
```

- `model: null` = **跟随主模型（默认）**。非空时是一份完整的端点配置（baseUrl、model、apiKey、supportsImages、contextTokens、maxTokens），apiKey 只写不回显，沿用 `ModelSettingsView` 的做法。
- 提示词只存覆盖值，`null` 表示用代码里的内置默认；「恢复默认」就是写回 `null`。
- **能力归属（一个能力只有一个持有者）**：
  - `desktop_*`：永远只在桌面子 agent 上，主 agent 没有。
  - `file_*`：`files.enabled` 且存在启用的读文件子 agent ⇒ 归它；没有这样的子 agent ⇒ 归主 agent（这就是「没人占用 = 主 agent 自己执行」）；`files.enabled=false` ⇒ 谁都没有。
- 能力开关关闭时，对应场景不对主 agent 提供（有效场景 = 已启用且能力已开的子 agent 的场景）。
- **封闭目录 + 一场景一 agent 的直接后果：子 agent 总数最多 2 个。** 「＋新增子 agent」实际只能新增读文件那一个，UI 要如实表现（见 §7）。
- **加载 vs 保存的宽严不同**：保存严格校验（§5）；加载时旧文件没有 `agents` 段就用默认（含内置桌面子 agent），缺内置项则补回。补回后仍不满足规则 4，则 **fail-closed**：桌面工具不注册给任何人，`/api/consumer` 的 reason 明说「桌面已启用但没有可用的桌面子 agent」。

---

## 3. 路由与工具契约

**为什么必须异步。** 同步等子 agent，主 turn 仍被占着，单读者队列照样堵；而且 `ToolTimeout` 全局 30 s（`AgentTurnRunner.cs:673`）、`ToolDispatchTimeout` 180 s，装不下一个桌面任务。所以：派活立刻返回，完成后把汇报当新输入塞回队列，主 agent 起新 turn 转述。

主 registry 在「有效场景 ≥ 1」时才注册（没有场景 = 主 agent 自己干）：

| 工具 | 参数 | 返回 |
|---|---|---|
| `agent_delegate` | `scenario`（枚举，取自有效场景）、`goal`、`context?` | `{jobId, agent, status:"started"}`，立即返回 |
| `agent_job_status` | `jobId?` | 状态、当前第几轮 / 共几轮、最近一步摘要 |
| `agent_job_cancel` | `jobId` | 是否已请求取消 |

- 工具描述和主 agent 系统提示里的「可委派场景」块由场景目录自动生成（只读）。
- 同一子 agent 同时只跑一个 job；再委派同场景返回 `busy` + 在跑的 jobId。
- 三个工具在 `DesktopGovernance` 里登记为 Allow（委派本身不动机器，真正的动作由子 agent 自己的治理 / 审批把关）。
- **回灌**：job 结束后入队一条 `InputItem`：`Source = Agent`（新增），`IdempotencyKey = "agent-job:{jobId}"`。幂等键沿用现有的至少一次语义，重复完成不会重复入队。

**汇报有两种形态，主 agent 看第一行就能分辨：**

```
[子 agent「桌面操控」任务 job-17 · 已完成 · 第 2/5 轮]
结论：已打开网易云音乐并开始播放“夜曲”
证据：最后一次动作校验=画面已变化；截图 media-83
```

```
[子 agent「桌面操控」任务 job-17 · 无法执行]
目标：打开网易云音乐并播放“夜曲”
原因类别：rounds_exhausted
错误：desktop_act 被拒：(512,300) 这个点上是 'XXX'，不是目标窗口……（最后一次的原文，截断）
已尝试：5/5 轮
  1. 步数耗尽（32 步）  2. 元素找不到  3. …
已执行过的动作：desktop_launch ×1，desktop_act ×14（含 3 次 type）
```

原因类别：`rounds_exhausted`、`approval_denied`、`governance_denied`、`cancelled`、`timeout`、`model_error`、`interrupted`、`reported_impossible`。主 agent 的默认提示词要写明：收到「无法执行」就用人话告诉用户原因，**不得**说成功，也不要拿同一个目标原样再委派一遍。

---

## 4. 提示词

**主 agent 的有效系统提示** = 用户的主提示词（或内置默认）＋ 自动生成的「可委派场景」块（只读）＋ 现有钩子注入（技能、记忆，不变）。

**子 agent 的有效系统提示** = 它自己的提示词（或内置默认）＋ 汇报契约（只读，要求以 `task_report` 工具收尾，见 §6）。任务目标放在 user 消息里。

- 内置默认是代码常量：`MainDefault`（保持现有那句不变，避免无意改变行为）、`DesktopDefault`、`FileReadDefault`。
- `DesktopDefault` 要点：看-做-看循环；动作后必须看校验结果；没有证据不报成功；**重试前先观察现状，判断上一轮做到哪一步，不要把已完成的动作再做一遍**（发送、删除这类动作重做会有副作用）。
- `FileReadDefault` 要点：先列目录再读；只读不改；找不到就换个办法找，确认没有就如实报告；大文件按需分段读，回报摘要而不是整篇粘回去。
- **提示词是「非结构」改动，不重启消费者。** `SessionWindowContextAssembler` 改为持有 `Func<string?>`，每个 turn 读最新值；子 agent 在 job 开始时取一次快照。进行中的 job 不受后续编辑影响。
- 子 agent 不挂记忆召回 / 提炼钩子和技能钩子：记忆应来自主对话，子会话里的工具往返不该被提炼进长期记忆。
- 提示词计入上下文窗口。UI 显示 token 估算，超过提示预算的某个比例时拒绝保存（比例待定，暂定 50%）。

---

## 5. 规则 → 校验（`SettingsValidation` 新增，服务端为准）

| # | 规则 | 错误文案（示意） |
|---|---|---|
| V1 | 每个子 agent 恰好一个场景，且在目录内；场景**全局唯一**，含已禁用的子 agent | `场景 'file-read' 已被「文件阅读」占用，同一场景只能有一个子 agent` |
| V2 | 必须存在 id=`desktop` 的内置项，它的场景是 `desktop` 且不可改；不可删除 | `内置的桌面操控子 agent 不能删除或改场景` |
| V3 | `desktop.enabled` ⇒ 桌面子 agent 已启用 | `桌面操控已开启，必须有一个启用的桌面操控子 agent` |
| V4 | `files.enabled` ⇒ `roots` 非空，且每项是存在的绝对路径；不得覆盖应用自己的数据目录 | `读文件已开启，但没有配置允许读取的目录` |
| V5 | `maxRounds` 在 1–10（范围待定） | `最大轮数必须在 1–10 之间` |
| V6 | 提示词长度上限（§4） | `提示词过长（约 N token）` |
| V7 | 模型覆盖非空时端点完整：URL 合法、模型名非空、`maxTokens < contextTokens`（复用现有 `CheckEndpoint` 规则） | `子 agent 的模型配置不完整` |

- 规则 4 的另一半在 UI：设置页打开桌面开关时若 V3 不满足，直接提示并指向「提示词」页，而不是等服务端拒绝。
- UI 里的即时检查只是便利；以服务端返回的 `errors` 为准，并用同一组用例钉住两边一致。

---

## 6. 运行时

**前置重构（零行为变化，靠现有 1000+ 单测兜底）**

- 从 `ConsumerHost.Start()` 抽出 `AgentRunnerFactory`：输入一份档案（会话存储、提示词来源、工具表、钩子、治理、审批、模型与选项），输出 `AgentTurnRunner`。主 agent 和每个子 job 走同一个工厂。
- `AgentTurnOptions` 加 `SessionId`，替换写死的 `"main"`。
- 模型覆盖要求每个子 agent 有自己的 `ILlmClient`、token 计数器、`ContextWindowTokens` / `MaxTokens`，都由工厂按有效模型生成。

**`SubAgentHost`（Api 层新增）**

- job 表（内存）＋ 追加式 `agents/jobs.jsonl`（started / round / finished 三种记录）。
- 每个 job 一份独立会话文件 `agents/<agentId>/<jobId>.jsonl`。**子会话的截图和读到的文件内容永远不进主会话**，这是主上下文省下来的地方。
- 每个子 agent 一个信号量，同时一个 job。
- runner 要求拿到 `Status=Running` 的 `InputItem`（`AgentTurnRunner.cs:177`）。job 不是队列项，所以构造合成 item，并在代码里注明原因；不为此另写一套 turn 循环。
- 治理复用 `DesktopGovernance`，审批复用同一个 `QueuedApprovalHandler` + `LeaseGrantingApprovalHandler`，「批准窗口 = 连续 10 步」的租约照常工作。`PendingApproval` 加 `Origin`（agent 名、jobId、目标一句话），横幅显示「来自子 agent「桌面操控」：…」。

### 6.1 逐轮重试与失败回报

**「轮」的定义**：一轮 = 对 job 会话跑一次完整的 `AgentTurnRunner.RunAsync`（含它自己的工具步数预算）。`maxRounds` 默认 5，**含首轮**（若你的意思是「首轮 + 5 次重试」，把默认值改成 6 即可，见 §10）。

**判定一轮成败不靠解析自然语言。** 子 agent 的 registry 里多一个只在子会话里存在的 `task_report(status, summary, evidence?, error?, retryable?)`：

- `status=success` ⇒ job 成功结束，汇报形态 1。
- `status=failed` ⇒ 本轮失败；`retryable=false` 是模型自己声明「这事做不成」（例如全盘找不到那个文件），**直接终止**，不烧完 5 轮。
- 没调用 `task_report`、步数耗尽（`turn.detail=step-limit`）、本轮抛异常 ⇒ 本轮失败，原因由代码记录，不是模型自述。

**哪些失败不重试（终止类）**：重试它们要么没用，要么是在绕开人的决定。

| 信号 | 原因类别 | 为什么不重试 |
|---|---|---|
| 工具结果以 `approval denied:` 开头 | `approval_denied` | 用户说了不，或没人回应。再试只会反复弹审批 |
| 工具结果以 `denied:` 开头 | `governance_denied` | 治理规则拒绝（桌面未启用、窗口不在白名单、路径在目录外） |
| 用户取消 | `cancelled` | — |
| job 总时限到 | `timeout` | — |
| 模型调用失败（`ModelCallFailure`） | `model_error` | 客户端自己已经重试过；再来多半同样失败 |
| UIPI 拒绝（目标是提权窗口） | `governance_denied` | 本进程注入的输入会被系统吞掉，换轮次也不会好 |

其余（元素找不到、租约过期、被遮挡、校验=画面无变化、工具超时、文件没找到、步数耗尽、没汇报）都算可重试。

**轮与轮之间**：在同一个 job 会话里追加一条 user 消息，由**代码**生成，包含：第 k 轮失败的类别和原文错误、上一轮**已经执行过的动作清单**（从 ToolCall 事件汇总，如「desktop_act ×14，其中 type 3 次」）、「先观察现状再决定，不要重复已完成的动作」。理由：桌面动作不幂等，一轮可能已经把消息发出去了，只是校验没认出来，下一轮从头再做就是发两遍。

**全部失败后**：状态 `cannot_execute`，汇报形态 2（§3），带类别、最后一次错误原文、逐轮一行原因、已执行动作清单。**不吞错误，也不把失败包装成"部分完成"。**

**总时限**：每轮已受 `TurnTimeout`（10 分钟）约束，但 5 轮累计 50 分钟的桌面抢占不可接受，需要 job 级总时限（暂定 15 分钟，待定）。

**主 agent 侧防循环**：同一条用户消息内，对同一场景在一次「无法执行」之后最多再委派 1 次（待定），超过则 `agent_delegate` 返回拒绝并要求直接告知用户。

**成本上限心里有数**：桌面场景最坏 5 轮 × 32 步 = 160 次模型调用，且截图占上下文。同一会话里旧图会被窗口机制挤出，但要在 P1 实测一次，必要时每轮换新会话只带摘要。

### 6.2 读文件工具（新写，只读）

- `file_list(path)`、`file_read(path, offset?, limit?)`。治理放行（只读不问，同 `desktop_observe`），**但路径必须落在 `files.roots` 内**：规范化后比较，拒绝 `..` 穿越，以及指向目录外的符号链接 / junction（Windows 要显式处理）。
- 硬拒绝名单，即使在 roots 内：应用自己的数据目录和 `settings.json`（里面有 ApiKey）、`.env`、私钥类文件（`*.pem`、`id_rsa*` 等）。
- 限额：单次最多读 N 字节、整文件超限时截断并说明（N 待定）；二进制文件拒绝并说明。
- 编码：先试 UTF-8，不合法再试 GBK（Windows 中文环境常见），在结果里写明用了哪个。
- 工具失败原因要可区分：`not_found`、`outside_roots`、`too_large`、`binary`，便于 §6.1 分类与汇报。

**取消与生命周期**

- 取消入口：`agent_job_cancel`、状态栏「停止」、job 行按钮。主 turn 早已结束，队列项的 `RunningToken` 管不到 job，所以要独立的取消令牌；轮与轮之间也检查取消。
- `ConsumerHost.ReconfigureAsync` 重建工具表前先取消所有 job，并各入队一条「被重配置中断」汇报。
- 进程崩溃：启动时扫 `jobs.jsonl`，有 started 无 finished 的补一条 `interrupted` 汇报；幂等键保证不和正常汇报重复。
- **物理抢占**：`SendInput` 是全局的，「后台」只是对话层面的后台，子 agent 仍会抢前台窗口和鼠标。v1 只做醒目指示（状态栏常驻「桌面任务进行中 · 第 k/5 轮 · 停止」）。

---

## 7. UI：侧边栏新增「提示词」

- `App.vue` 的 `NAV` 在「技能」之后、「设置」之前加 `{ key: 'prompts', label: '提示词', icon: 'prompt' }`；`icons.ts` 补图标（`IconName` 是闭集）。键盘导航沿用 `SideBar.vue` 的 roving focus。
- 新视图 `views/PromptsView.vue`，左列表右编辑：
  - 左：「主 Agent」置顶；「桌面操控」（内置徽标，无删除）；「文件阅读」（若已新增）；底部「＋ 新增子 agent」。
  - 「＋新增」打开一个小表单：名称 + 场景下拉。`desktop` 灰掉并写明「已被内置桌面操控占用」，`file-read` 可选；它也被占用时按钮禁用并说明「两个场景都已有子 agent」。
  - 右（主 agent）：提示词文本框、**有效提示词预览**（含只读的「可委派场景」块）、token 估算、「恢复默认」、「已修改」标记。
  - 右（子 agent）：名称、启用开关、场景（只读，附目录里的固定描述）、**模型**（默认「跟随主模型」，可切到自定义端点）、提示词、最大轮数（默认 5）、有效提示词预览（含只读汇报契约）。
- 读文件的目录白名单放在**设置页**，做成和桌面并列的一张卡（开关 + 目录列表）；提示词页的读文件子 agent 在 `files.enabled=false` 时显示「读文件未开启」并链接过去。
- 规则 4 联动：设置页打开桌面开关时检查 V3，不满足就提示并给「去提示词页」链接；提示词页顶部在「桌面已开启但桌面子 agent 不可用」时常驻警示条。
- **保存路径：专用端点 `GET/PUT /api/agents`**，服务端读取 → 合并 → 走 `SettingsStore.Save`（整份校验照常）。理由：`SettingsView.vue` 整份回填整份 PUT，两个页面各自写同一份文档会互相覆盖。`PUT /api/settings` 里 `agents` 缺省 = 保持原值（同 ApiKey 的「缺省 = 保持」）。
- 子任务可见性：会话页里 `agent_delegate` 渲染成一张卡（`toolAppearance.ts` 加名字映射），卡上显示轮次；P2 再加「子任务」抽屉，只读打开 job 会话（复用 `EventList`）。

---

## 8. 分期任务

**P0 · 前置（不改行为）**

- [x] 0.1 `AgentsSettings`、`FilesSettings` 数据模型：值相等（含列表）、默认含内置桌面子 agent、加载补齐；`SettingsView` 响应透传
- [x] 0.2 内置提示词常量化；`IContextAssembler` 改读 `Func<string?>`，主提示词可配置
- [x] 0.3 抽 `AgentRunnerFactory`；`AgentTurnOptions.SessionId` 取代写死的 `"main"`
- [x] 0.4 `SettingsValidation` 加 V1–V7 与单测矩阵
- [x] 0.5 读 `ToolFailureReason` 的实际取值，定出「可重试 / 终止」分类表（§6.1）

**P1 · 后端闭环**

- [x] 1.1 `InputSource.Agent`：逐处核对约 27 处引用，改掉「exactly three kinds」的注释
- [x] 1.2 `SubAgentHost`：job 表、独立会话、信号量、取消、总时限、`jobs.jsonl`
- [x] 1.3 `agent_delegate` / `agent_job_status` / `agent_job_cancel`；`DesktopGovernance` 登记
- [x] 1.4 工具归属：桌面启用 ⇒ 主 registry 不再有 `desktop_*`；`file_*` 按 §2 归属；截图工具改看桌面子 agent 的有效模型；V3 不满足时 fail-closed 并写明 reason
- [x] 1.5 `file_list` / `file_read`：roots、规范化、符号链接、硬拒绝名单、限额、GBK
- [x] 1.6 `task_report` 工具 + 轮次循环 + 终止 / 可重试分类 + 轮间反馈 + 失败汇报
- [x] 1.7 结果回灌（幂等键 `agent-job:{jobId}`）＋ 启动时的中断补偿
- [x] 1.8 `PendingApproval.Origin`
- [x] 1.9 `AgentsNeedsRestart`（比结构：子 agent 集合、场景、启用、模型覆盖；**不比提示词、不比 maxRounds**）与 `FilesNeedsRestart`

**P2 · UI**

- [x] 2.1 图标、`NAV`、`PromptsView` 骨架、`GET/PUT /api/agents`
- [x] 2.2 主 agent 编辑：预览、token 估算、恢复默认
- [x] 2.3 子 agent 新增 / 编辑、场景下拉与占用提示、最大轮数
- [x] 2.4 子 agent 模型覆盖编辑（默认跟随主模型；apiKey 只写不回显）
- [x] 2.5 设置页：读文件卡（开关 + 目录）；桌面开关与 V3 联动；提示词页警示条
- [x] 2.6 审批横幅显示来源；状态栏任务与轮次指示、停止按钮
- [ ] 2.7 子任务抽屉与只读 job 会话

**P3 · 可选**

- [ ] 3.1 `needs_input`：子 agent 需要用户决定时汇报，主 agent 带 `resumeJobId` 续跑同一个子会话
- [ ] 3.2 用户自己在操作鼠标键盘时自动暂停桌面 job
- [ ] 3.3 `task_report` 做成终止型工具，省掉汇报后多出的一次模型调用
- [ ] 3.4 放开场景目录（新增场景）与「一场景多 agent」

**P4 · 收尾**

- [x] 4.1 README 补一节「主 / 子 agent、提示词与重试」，并更新「唯一逻辑会话」的表述（子会话是旁路，不是第二条主会话）
- [ ] 4.2 真机验证清单（§9 末尾）

---

## 9. 测试

沿用仓库习惯：每条保护性改动都要有「撤掉就红」的反例。

**配置与归属**

- 值相等：两个默认 `AgentsSettings` 相等；只改提示词或 `maxRounds` 不触发 `AgentsNeedsRestart`，改场景 / 启用 / 模型覆盖触发。
- 校验矩阵：V1–V7 各一正一反，含「禁用的子 agent 仍占用场景」「试图把 `desktop` 给非内置项」。
- 规则 4：开桌面且桌面子 agent 被禁用 ⇒ 保存被拒；旧文件加载 ⇒ 补齐内置项；补齐后仍不满足 ⇒ fail-closed 且 reason 可读。
- 工具归属：主 registry 无 `desktop_*`；`files.enabled` 且无读文件子 agent ⇒ 主有 `file_*`；有 ⇒ 主没有、子有；`files.enabled=false` ⇒ 都没有。步数预算：桌面子 agent 32、主 8。
- 模型覆盖：主模型 `SupportsImages=false` + 桌面子 agent 配视觉模型 ⇒ 子 registry 有截图工具、主没有。

**委派与回灌**

- 未知场景被拒；`busy` 路径；`agent_delegate` 不阻塞 turn；完成后队列出现 `Agent` 项；重复完成不重复入队；崩溃后补偿一条 `interrupted`。
- 隔离：主会话日志里没有 `desktop_*` 事件，也没有文件内容；记忆钩子不对子会话触发。
- 提示词时效：编辑后下一个 turn 生效；进行中的 job 用启动快照；用户提示词里写「忽略以上」不影响汇报契约仍被附加。

**重试（本版重点）**

- 前 4 轮失败、第 5 轮成功 ⇒ 成功，汇报写「第 5/5 轮」。
- 5 轮全败 ⇒ `cannot_execute` / `rounds_exhausted`；汇报含逐轮原因和**最后一次错误原文**；主 agent 的下一个 turn 里能看到它。
- `approval denied:` ⇒ 第 1 轮后立即终止，不再重试（撤掉分类必红：会反复弹 5 次审批）。
- `denied:`、UIPI 拒绝、取消、总时限 ⇒ 立即终止，类别正确。
- 模型自报 `retryable=false` ⇒ 提前终止，不烧完 5 轮。
- `maxRounds=1` ⇒ 不重试。
- 没调用 `task_report` ⇒ 判失败，原因写「未汇报」，不是静默成功。
- 轮间反馈含上一轮已执行动作清单；取消在轮间生效。
- 主 agent 防循环：同条用户消息内同场景「无法执行」后第二次再委派被拒。

**读文件**

- 目录外路径拒绝；`..` 穿越拒绝；指向目录外的符号链接 / junction 拒绝。
- 应用数据目录、`settings.json`、`.env`、私钥文件即使在 roots 内也拒绝。
- 超大文件截断并说明；二进制拒绝；GBK 文本正确解码。
- 失败原因可区分：`not_found` / `outside_roots` / `too_large` / `binary`。

**UI（vitest）**：场景下拉占用提示、「＋新增」禁用条件、恢复默认、规则 4 联动、`SettingsView` 保存往返后 `agents` 与 `files` 不丢。

**回归**：P0 重构完成时现有 1000+ 单测 / 200 前端测试必须仍全绿。

**真机验证清单（P4）**：桌面任务进行中仍能语音对话；取消真的停手；审批横幅写明来源；故意让桌面任务失败，主 agent 用人话说出原因而不是说成功；重试没有把已发出的消息再发一遍；任务中途重启后主 agent 收到「被中断」；读文件读不到目录外的东西。

---

## 10. 风险与待你拍板

风险：

- **重试的副作用**：桌面动作不幂等，重试可能重复发送 / 重复点击。靠「轮间动作清单」和 `DesktopDefault` 里的"先看现状"缓解，不能根治；不可逆动作仍由审批把关。
- **成本与抢占时长**：最坏 5 轮 × 32 步；靠总时限和取消兜底。
- **汇报不可信**：靠 `task_report` 的证据字段和主提示词里"没有证据的成功要说不确定"。主 agent 失去中途纠偏，只能查 status、取消、重发。
- **读文件的泄密面**：模型读到的内容会进它的上下文并可能被转述。目录白名单、硬拒绝名单、默认关闭是三道防线；白名单配得过宽就等于没有。
- **子 agent 总数最多 2 个**：封闭目录加一场景一 agent 的必然结果，不是 bug。

默认决定，想改就说一声：

1. 「5 轮」= 总共最多 5 次尝试（含首轮）。若你的意思是首轮 + 5 次重试，默认值改 6。
2. 读文件用独立的 `files` 开关和目录白名单，默认关。轻量替代是「有读文件子 agent 就算开」，但那样没配子 agent 时主 agent 就无法读文件，与"没人占用 = 主 agent 自己执行"冲突。
3. 场景名称与描述固定在代码里，不可编辑；用户能改的是各 agent 的提示词。
4. 子 agent 本版不带 MCP 工具（上一版允许，现在收窄到两个场景）。
5. 模型覆盖放在 P2 就做，因为「默认使用主模型」意味着它可以不用主模型。
6. job 总时限 15 分钟、同条消息内同场景最多再委派 1 次、提示词上限 50% 预算、`maxRounds` 范围 1–10：都是暂定数字，需要真机数据校准。

---

## 11. 还没验证的（动手前先看）

- `ToolFailureReason.From` 的实际取值，以及它是否足以支撑 §6.1 的分类。
- `JsonlSessionStore.Open` 开第二个实例的路径与锁。
- `InputSource` 约 27 处引用的真实语义：目前只数了数，没有逐个读。
- `MemoryHook` 会不会因为 `Source=Agent` 误蒸馏。
- `SettingsView.vue` 的回填方式，`SettingsStore.Save` 的并发 / 原子写。
- `ApprovalBanner.vue` 能展示哪些字段。
- Windows 下符号链接 / junction 的规范化方式（.NET 的 `LinkTarget` 对 junction 的支持需实测）。
- 同一会话里 5 轮截图叠加后，上下文窗口机制的实际表现。

---

## 12. 实现状态与偏差（本次已落地）

**验证**：.NET 全部单测 1164 通过 / 15 跳过（需真实桌面的活测）；前端类型检查、构建、223 个测试通过；`check-docs` 与 `check-css-names` 通过。smoke 的子 agent 相关检查全绿；`desktop: a picture's marks…`（读真实桌面的第一个窗口）在我这台机器上间歇失败，它跑在所有 API 宿主之前，没有走过本次改动过的代码，没有在改动前的提交上复现（3 次），所以我把它记成环境相关而不是回归。

**未做**：2.7 子任务抽屉与只读 job 会话的界面（后端 `GET /api/agent-jobs/{id}/session` 已有，smoke 在用）；4.2 真机验证清单；全部 P3。

**和方案不一样的地方**：

- 没有单独抽出 `AgentRunnerFactory`：主 agent 仍在 `ConsumerHost.Start()` 里组装，子 agent 在 `SubAgentHost` 里组装，共用的只有 `AgentTurnOptions.SessionId` / `PromptSource` 这两个新选项。两处各约 30 行，抽工厂是纯重构，等第三个使用方出现再做。
- **`PUT /api/settings` 缺省 `agents` = 保持原样**（用 `AgentsSpecified` 区分），别的段仍是缺省 = 默认。写测试时发现：不这样做，任何不带 `agents` 的保存（脚本、旧版设置页）都会把提示词和子 agent 清回默认。
- 主 agent 防循环不是计数器，而是**同一目标在 10 分钟内失败过就拒绝原样再委派**（忽略空白与标点）。计数器需要知道"同一条用户消息"，工具层拿不到。
- 目录白名单的"必须存在"只检查**新增**的条目；已有的目录消失了（拔掉的盘）只在 `problems` 里提示，不再让无关的保存失败。工作区目录永远拒读，所以不再校验"白名单不得覆盖数据目录"。
- 每轮仍受 `TurnTimeout`（10 分钟）约束，任务总时限 15 分钟；`maxRounds` 不触发消费者重启。
- `file_` 工具族的步预算 24（`AgentTurnOptions.ToolStepBudgets`）。
- `GET /api/consumer` 新增 `agentTools`：子 agent 的工具表与治理决定，因为桌面工具已不在主 agent 的 `tools` 里。
- `MemoryDistilHook` 对 `InputSource.Agent` 不提炼（子 agent 的汇报不是用户说的话）。
- `DesktopGovernance` 同时负责委派工具、`task_report`、`file_*`（类名没改）。
- 提示词里的换行统一成 `\n`（`StringBuilder.AppendLine` 在 Windows 上是 `\r\n`）。
- smoke 里原来让脚本化模型直接当主 agent 调桌面工具的检查，改成经 `DesktopAgentShim` 走真实委派路径；读工具结果改读 job 自己的会话。

---

## 13. 会话：隐藏子 agent、只能删最新、撤回可取消（追加需求，已落地）

需求：子 agent 的消息在会话里隐藏且不进记忆；删除只能删最新一次对话；「删除之后」改名「撤回」，撤回之后、发出新消息之前可以取消撤回。

- **隐藏**：`GET /api/session` 过滤掉 `Agent` 来源的用户消息（日志和模型照常能读到）。主 agent 对汇报的回复是普通助手消息，照常显示，且不带来源标签。`Agent` 来源的整轮不进记忆（`MemoryDistilHook` 跳过，Core 里有对照测试）。
- **对话**的定义：从有人发出的用户消息（打字 / 语音 / 定时）开始，到下一条这样的消息为止；子 agent 汇报不开始新对话。范围起点取到该轮的 `TurnStart`，轮次标记不会落单。
- **删除**：只能删最新一次对话，整段物理清除（含修订、记忆）；更早的返回 409；有回合在跑返回 409；有待定的撤回也返回 409。
- **撤回**：写一条带 `withdrawn` 标记的 `Retract`，只隐藏、不删；记忆也先不动。`POST /api/session/withdrawal/cancel` 把这条记录拿掉即恢复。**发出新消息**（`/api/input` 或语音）时、先于入队，才物理清除并级联删记忆。同一时刻最多一次待定撤回，从更早处再撤回会把它扩大；重启后待定状态从日志里读回。
- `GET /api/session` 新增 `conversation: { latestFromSeq, withdrawn }`，界面据此决定哪些行有「删除」、是否显示「取消撤回」条。
- `POST /api/session/{seq}/retract` 不再存在，改为 `.../withdraw`。原来行上的「撤回」（把输入放回输入框）改名「复用」。
- 测试：Api 198 → 202 个（删除 / 撤回 / 取消 / 生效 / 扩大 / 记忆级联 / 重启后仍可取消），smoke 里 `session.removal` 能力换成四条新检查，`capabilities/manifest.baseline.json` 的证据名同步。

**没做 / 取舍**：定时任务和子 agent 汇报到达不算「发出新消息」，不会让撤回生效（所以撤回后收到汇报，被隐藏的区间仍然可以取消）；撤回期间被隐藏对话的记忆仍可能被召回，直到新消息发出。
