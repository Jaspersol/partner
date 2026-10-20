# Todo — 七条主流程的性能优化与能力强化（方案稿，分支 local-voice-stack）

范围：桌面操控、上下文压缩、记忆、联网搜索、主/子 agent 委派、语音输入输出、定时任务。每条都来自读代码（行号为当前工作区，基于 92380a4），格式统一为「证据 → 问题 → 方案 → 验证」。上一版 todo（联网搜索方案）已在 2a61853 落地，原文见 git 历史。

优先级：P0 = 默认路径上已经坏或明显拖慢体验；P1 = 显著收益、改动可控；P2 = 值得做、可排后；P3 = 清理与长期项。

---

## 0. 建议执行顺序

第一批（P0，互相独立，可并行）：

1. ~~M1 记忆 FTS 分量恒为 0~~（已完成）
2. ~~M2 中英混合查询丢一半~~（已完成）
3. ~~C1 回合内无预算控制 + C2 无自动压缩~~（已完成）
4. ~~V1 首音要等整段回复合成完~~（已完成）
5. ~~S1 失败任务 50ms 无限重试~~（已完成）
6. ~~D1 桌面 observe 过滤只筛结果~~（已完成）；D2 act 动作链过重（部分完成，见下方落地备注）

第一批落地备注（与方案的差异与遗留）：

- S1：保持"Failed 仍可调度"的既有契约（`JsonlSchedulerTests` 与设计文档 §2.4 都固化了它），所以没有把 Failed 移出 due 集；改为内存中的重试时间 + 1s→5min 指数退避（`SchedulerOptions.RetryBaseDelay/RetryMaxDelay`），重启后立即重试一次。需要"移出自动 due 集"的话，要先改这条契约。
- C1：单条工具结果上限为提示预算的 1/5（下限 512 token），截断发生在写入会话日志之前，下一回合回放不会被一条超大记录挤掉更早的历史（因此界面里看到的是截断后的文本）。累计超 80% 时折叠最早的工具结果，直到 60%；最新一步不折叠。`AgentTurnRunner` 新增可选 `tokens` 参数，`ConsumerHost` 传入当前计数器；子 agent 仍用本地估算。
- C2：`TurnLoop` 新增 `afterTurn` 回调，在单读者循环里、回合结算之后执行；`ConsumerHost` 用它在 `Fraction > 0.8` 或 `FoldedItems > 0` 时调用 `CompactAsync`。回调失败不影响已结算的条目。未写"超预算后 Summary + Retract"的端到端用例，只覆盖了回调时序与失败隔离。
- V1：`LiveSpeech` 按句末边界从 `LiveAssistant` 取已写出的句子，第一句合成完且（第二句合成完或 1.5s）即放闸，后续句子边合成边播；日志里的 Assistant 事件只补尚未说出的部分。"首个 delta → 首次出声 < 1.2s"没有在真机上测过（依赖真实 TTS 的 RTF），只验证了顺序与不重复。
- D1：`desktop_observe` 不带过滤时只展开前台窗口和刚操作过的窗口，其余窗口标 `elementsOmitted`；新增 `all=true` 取回整桌面。
- D2：只做了两项：坐标类动作在租约有效且窗口矩形没变时不再重走 UIA 树（`ObserveForAct`），以及 `feedback` 默认改为 `on-failure`。"能读状态校验的动作跳过 before 帧"依赖 D3（读 Toggle/Value/DOM 状态做校验），此处未做；否则会丢掉校验。

第二批（P1 中的"静默失效"类，已完成）：W1 搜索预算被工具超时截断、A3 子 agent 超时留下孤儿回合、A2 web 子 agent 步数只有 8、S2 调度持锁入队、C5 提示缓存失效（`cache_control` 部分未做，见下方备注）。

第二批落地备注：

- W1：新增 `ITimedTool`（工具自带时限，只延长不缩短）；`web_search` 的时限 = 搜索预算 + 5s，预算设到 60s 不再在 30s 被截断。Fanout 本来就在预算处收尾并返回已到结果，所以没有改它。
- A2：默认 `ToolStepBudgets` 加了 `["web_"] = 24`，主 agent 持有 web 工具时同样适用。"按场景配置"没做。
- A3：每轮用独立的链接 token，超时先 `Cancel()`，再等 5 秒（`JobEngine` 的 `cancelGrace`）让回合退出，之后才汇报超时。仍不退出的回合照旧放弃。注意原本超时就是终态（不会再开下一轮），所以"与下一轮并发写入"并不会发生，真正的问题是汇报之后孤儿回合还在调模型、写子会话。
- S2：`DeliverDueAsync` 不再整段持 `_gate`：锁内只取 due 快照，入队在锁外并有 `EnqueueTimeout`（默认 10s，超时按失败进退避），写回执与推进状态时再回锁；另加 `_deliverGate` 保证同一时刻只有一轮投递。入队期间任务被删除/暂停/改期，则只跳过记账。
- C5：召回块（label=`recall`）从 system 移到当前输入之前，作为同一条 user 消息的"背景资料"前缀，system 消息只剩人设、技能列表等稳定内容；连续两回合 system 字节相同。选择并入最后一条 user 消息而不是新增一条 system/user 消息，是因为中途出现的 system 消息会被部分本地模型的聊天模板拒绝，连续两条 user 消息会被严格交替的模板拒绝。这改变了 `MemoryHook` 里"召回放进 system 块"的原有设计意图，需要真实模型观察一下行为。**未做**：`HttpLlmClient` 的 `cache_control` 断点（依赖 provider 支持，需要先确认实际使用的服务）、"记录响应里的 cached tokens"。

第三批落地备注（已做的 P1 和各自的缺口；D3 / D4 / D5 / X1 未做）：

- C3：压缩器与装配器用同一套"这条算对话"和计价文本（`IsConversational` / `CostText`），`SessionCompactor.HistoryBudget(usage)` 先扣掉 system 再给历史预算；`ContextUsage` 新增 `SystemTokens`。设置页缩小窗口触发的压缩也改成按这个预算。
- C4：摘要改为分行结构（目标 / 已定 / 事实 / 未决 / 进行中），长度随预算：预算的 1/6，最多 512，窗口够大时至少 64，并从历史预算里先预留出来，压缩后不会又立刻溢出。覆盖范围本来就记在 Summary 的 `coveredFrom/To`。**未做**：把摘要里的稳定事实同步写进 Semantic 记忆。
- M3：`MemoryScorer.Tokenize` 对中文 run 用二元组，改写过的句子也有 lexical 得分。
- M4：`synchronous` 改为 NORMAL（WAL）；批量写入先算 embedding 再开事务；提升扫描限频为每 30 秒一次（`PromotionThrottle`），所以"紧接着的下一次写入就提升"的契约变成"超过限频窗口后的下一次写入"。
- M5：召回只用一条 `UPDATE ... IN (...)` 标记已用；`DemoteIdle` 不再跟在召回后面，只留在维护通道。
- M6：召回按归一化正文去重。**未做**：写入前按内容哈希去重（`MemoryLifecycle` 明确把"完全相同"当作不冲突，改它要先改这条约定）。
- M7：`ApproximateTokenCounter` 中日文按一字一 token 计；`MemoryStoreOptions.MinScore`（应用里设为 0.3）；召回 query 取输入的前 300 字。**未做**：MMR 多样性。
- W2：snippet 最多 300 字、命中列表约 6000 字后截断并说明省略了几条；`web_fetch` 本来就分段。
- A1：同一子 agent 可排最多 2 个后续任务（状态 `queued`，工具结果里写"前面还有 N 个"），第 4 个才按 busy 拒绝；排队中的任务可取消。
- A4：汇报给主 agent 的结论 ≤ 1500 字、证据 ≤ 2000 字，超出处写明原文字数和 `/api/agent-jobs/{id}/session`。
- A5：委派时把最近 4 轮对话（共 ≤ 1500 字）作为"对话背景"附在子 agent 的补充信息里，标明只用来解析指代。**未做**：附件路径列表。
- D6：CDP 页面列表按 (端口, 标题) 缓存 1.5 秒，页面不应答时清掉；`CdpRegistry` 的探测共用一个静态 `HttpClient`。`Forget` 的调用点（端口存活巡检）没做。
- D7：`desktop_observe` 新增 `maxElements`（默认 40，上限 200）、`roles`、`nameContains`；被截掉的数量在 `elementsMore`；value 最多 80 字。**未做**：翻页游标、有 elementId 时省略 `clickAt`。
- D8：截图标记清单改为 `号=名称(elementId)`，不再重复点击坐标。**未做**：JPEG/WebP、裁到变化区域、按剩余上下文自适应尺寸（这些需要在真机上看效果）。
- V2：**默认关闭**的 `BargeIn`（`VoiceInputSetting` / `RuntimeSettings.Input`，设置页还没有开关，改 `runtime.json`）；播放期间持续 600ms 超过静音阈值 2 倍的声音才算打断，打断后停 sink、丢未合成的句子、丢弃这条回复后续到达的文字，直到用户下一次开口。没有回声消除，所以只适合耳机。**未做**：KWS 唤醒词打断、把"播到哪一句"写进会话。
- V3：ASR 请求改为后台进行，tick 不再等它；进行中又来一块则按 `Busy` 保留欠的块，结果在下一个 tick 的开头取走。**未做**：interim 结果给 UI、流式模型。
- S3：`TurnLoop.onSettled` 回调把回合结果写回任务（`LastRunAt/LastRunOk/LastRunError`），`/api/schedules` 和 `schedule_list` 都带上。**未做**：界面里弹 Toast、语音播报。
- S4：`CatchUpPolicy`（once / skip / all）加在任务上并落盘，`schedule_create` 有 `catchUp` 参数；skip 超过 5 分钟宽限记为错过（`MissedCount`），all 最多补最近 5 个。**未做**：设置页的选项、Windows 计划任务开机拉起。
- 未做的原因：D3（状态读取式校验）、D4（`RuntimeId` 稳定元素 id）、D5（CDP 的 shadow DOM / iframe / 截图）都要在真实桌面和浏览器里反复验证，不适合只靠单元测试推进；X1 的共用预算已由 `ToolResultBudget` 覆盖 C1，其余工具各自定了上限，没有再抽象。

第三批：其余 P1 已做大半（D3 / D4 / D5 / X1 未做），P2 与 P3 整批未动。未完成项汇总在 §9。

每批完成前跑 `dotnet build Partner.sln` 和 `dotnet test`，涉及界面的改动用预览验证。

---

## 1. 上下文压缩 / 预算

- [x] **C1 [P0] 回合内没有预算控制** — 已完成
  - 证据：`AgentTurnRunner.cs:283-425` 每步把工具结果全文追加进对话（`:413`），上限 32 步（`AgentTurnOptions.cs:27`）；预算只在 `AssembleInitialAsync` 生效（`IContextAssembler.cs:104-131`）；`FinishReason=="length"` 只结束回合（`:437-441`）。
  - 问题：一次大 `file_read` / `web_fetch` 就能撑爆窗口，回合中途失败。
  - 方案：每步追加前按 `ContextBudget` 计价；单条工具结果超阈值时保留头尾、中间折叠为占位符（附"可用 offset 再读"提示）；累计超 80% 时把较早的工具结果替换为一行摘要。
  - 验证：脚本化 LLM + 超大 `ToolResult`，断言后续每次请求 token ≤ 预算且带截断标记。

- [x] **C2 [P0] 没有自动压缩** — 已完成
  - 证据：`CompactAsync` 只在 `ViewEndpoints.cs:976-981`（用户改小 contextTokens）与 `ConsumerHost.cs:300` 调用；溢出时装配器静默丢最旧条目（`IContextAssembler.cs:238-242`），`FoldedItems` 只上报（`:266`）。
  - 方案：回合结束后若 `Fraction > 0.8` 或 `FoldedItems > 0`，在 TurnLoop 串行通道里触发后台压缩（不与回合并发）。
  - 验证：超预算后跑一回合，断言出现 Summary + Retract，下一回合 `FoldedItems == 0`。

- [x] **C3 [P1] 压缩器与装配器计价不一致** — 已完成（见 §0 第三批备注）
  - 证据：`SessionCompactor.cs:59-63` 只用 `promptBudget`，没减 system/input（装配器在 `IContextAssembler.cs:236` 减了）；压缩器按 `entry.Body` 计价（`:61`），装配器按 `CostText`（含 handoff 参数，`:294-297`）。
  - 方案：抽一个共用计价函数（system、input、工具 schema、图片都算），两边共用。
  - 验证：`SessionCompactorTests` 加"压缩后 `MeasureAsync().FoldedItems == 0`"。

- [x] **C4 [P1] 摘要质量** — 已完成（见 §0 第三批备注）
  - 证据：`SessionCompactor.cs:93-103` 单段 512 token、无结构；旧摘要被再次摘要（`:82`）；与记忆不联动。
  - 方案：结构化摘要（目标 / 已定决策 / 事实与人名 / 未决问题 / 进行中任务），长度按预算比例；记录覆盖的 seq 范围；摘要中的稳定事实同时写入 Semantic 记忆。
  - 验证：两轮压缩后关键实体仍在；脚本化 LLM 断言输入含上次摘要。

- [x] **C5 [P1] 提示缓存不友好** — 已完成
  - 证据：system 块每回合重建（`IContextAssembler.cs:118-121,366-377`）；记忆召回以 priority 50 注入 system（`Hooks/MemoryHook.cs:97`），每回合内容都变，前缀缓存失效；`HttpLlmClient.BuildBody`（`:501`）不支持 cache breakpoint。
  - 方案：system 只放稳定内容（人设、工具规则）；召回改为历史末尾、用户消息之前的独立块；按 provider 支持加 `cache_control`。
  - 验证：连续两回合 system 前缀字节完全相同；记录响应里的 cached tokens。

- [ ] **C6 [P2] token 估算与计数开销**
  - 证据：`TokenCounter.cs:38-50` 按 4 字符/token，中文低估约 4 倍；provider 计数每条目一次 HTTP 且串行（`ContextBudget.cs:47-51`），kept/turnTokens 重复计（`IContextAssembler.cs:238-258`）；工具 schema 不计价（`:215-236`，`AgentTurnRunner.cs:323-327`）；`HttpLlmClient.cs:186-190` 明确不用 `usage.prompt_tokens`。
  - 方案：本地估算区分 CJK/拉丁；用响应 `usage.prompt_tokens` 校准系数；计数并行或批量；工具 schema 计入预算。
  - 验证：`TokenCounterTests` 加中文校准用例；断言一次装配 `/tokenize` 调用 ≤ 1。

- [ ] **C7 [P3] 微性能**：`ContextBudget.cs:52` `kept.Insert(0)` 是 O(n²)，改为反向收集后 Reverse；`TokenCounter.cs:127-140` 缓存命中仍对全文算 SHA-256，改用长度 + 快速哈希。验证：1 万条目基准。

---

## 2. 记忆

- [x] **M1 [P0] FTS 分量恒为 0** — 已完成
  - 证据：`MemoryStore.cs:955,968` 直接取 `bm25(...)`，真实匹配是负数；`MemoryScorer.cs:111-119` 把 `<= 0` 当"无匹配"返回 0，0.15 权重整体失效；`MemoryScorerTests.cs:114` 把 -3.5 → 0 固化成了预期。
  - 方案：`ComputeFts` 用 `-rank`（或 `1/(1+|rank|)`），"未参与"改用 `double?`/NaN 表示而不是 0；同步修正测试。
  - 验证：真实 store 召回，断言 `Reasons` 含 fts，且分数随匹配度单调。

- [x] **M2 [P0] 中英混合查询只走 CJK 索引** — 已完成
  - 证据：`MemoryStore.cs:403-405,169-171` 查询含任一 CJK 字符就只查 bigram 表；`CjkText.Bigrams`（`CjkText.cs:36-73`）只产出 CJK 片段，"部署 deployment" 的英文部分不参与；supersede 查重同样受影响。
  - 方案：两个 FTS 表达式都建，两次查询按 `record_id` 并集，rank 各自归一后取较优。
  - 验证：`CjkRecallTests` 加混合查询用例（只靠英文词能命中）。

- [x] **M3 [P1] CJK 词面相似度退化**：`MemoryScorer.cs:173-195` 把一整段中文当一个 token，0.30 权重的 lexical 只有整句相同才命中。改为复用 `CjkText.Bigrams`。验证：改写句（共享 bigram）lexical > 0。 — 已完成（见 §0 第三批备注）

- [x] **M4 [P1] 写入路径放大** — 已完成（见 §0 第三批备注）
  - 证据：每次 `WriteAsync` 先 `PromoteEligibleAsync`（`MemoryStore.cs:141`），逐 id `GetAsync`（`:575`）再插入，插入内嵌 embedding（`:791-798`）；语句各自自动提交且 `synchronous=FULL`（`MemorySchema.cs:26`）；`WriteBatchAsync` 在事务内算 embedding（`:279-285`）。
  - 方案：单事务批量写；WAL 下 `synchronous=NORMAL`；embedding 移出事务（先算后写，或后台补算）；提升改为定时/阈值触发。
  - 验证：语句计数测试（参照 `:349` 的 `SourceQueries`）；1000 条写入基准。

- [x] **M5 [P1] 读路径带写** — 已完成（见 §0 第三批备注）
  - 证据：每次召回对每条命中单独 `UPDATE last_recalled_at`（`:213-220`），再全表扫描 `DemoteIdle`（`:222,245-256`，该列无索引）；维护定时器每天已跑同一步（`MemoryMaintenance.cs:51`）。
  - 方案：一条 `UPDATE ... WHERE id IN (...)`；降级只留在维护通道；必要时给 `last_recalled_at` 建索引。
  - 验证：召回前后语句数与耗时断言；1 万行基准。

- [x] **M6 [P1] 同一句重复注入**：提升生成 episodic 副本但 working 行保留（`:583-592,611`），召回不按正文去重（`:434-453`）；内容完全相同也不算冲突（`MemoryLifecycle.cs:50-53`）。方案：召回按归一化正文去重并合并证据；写入前按内容哈希去重。验证：提升后召回同一正文只出现一次。 — 已完成（见 §0 第三批备注）

- [x] **M7 [P1] 注入预算单位与相关性门槛** — 已完成（见 §0 第三批备注）
  - 证据：`InjectionTokenLimit=5000` 用默认 `ApproximateTokenCounter`（`IMemoryStore.cs:9,24`；`ApiContext.cs:164-168` 未覆盖），中文实际约 4 倍；query 是整段 payload（`MemoryHook.cs:54`）；无最低分阈值（`MemoryStore.cs:434-453`）。
  - 方案：注入计价统一用 DualArm；加 `minScore` 与 MMR 多样性；query 截长（取用户原话 + 关键词）。
  - 验证：中文长正文注入后用真实 tokenize 计数 ≤ 上限；低分记录不注入。

- [ ] **M8 [P2] 衰减与向量**：temporal 只看 `created_at`（`MemoryScorer.cs:122-137`，`MemoryCandidate` 无 `last_recalled_at`，`MemoryTypes.cs:37-47`），常用的旧事实也在衰减；默认 `NullEmbeddingProvider`，hashing n-gram 与 lexical 高度重合（`Embeddings.cs:30-80`）。方案：用 `max(created, last_recalled)` 或访问加权；可选接本地/远端真实 embedding，按 `model_id` 迁移重算。验证：更新 last_recalled 后分数不降；语义改写用例能召回。

- [ ] **M9 [P2] 并发安全**：`MemoryStore` 共用一个 `SqliteConnection` 无锁（`:14,30-31`），维护 Timer 线程与回合线程并发使用（`MemoryMaintenance.cs:51-58`）。方案：`SemaphoreSlim` 串行化，或按操作开连接（连接池）。验证：并发 recall + maintenance 压测无异常。

- [ ] **M10 [P3]**：`MemoryHook.cs:109-120` 每回合物化全部 seq 建 HashSet，改为范围判断；`SupersedeConflictingAsync`（`:161-199`）对每个候选重复给 incoming 分词（`MemoryLifecycle.cs:63-68`），预算一次。

---

## 3. 联网搜索

已落地：7 个引擎插件、QueryPlanner、WebFanout、BatchGate、Merger、ResultFormatter、EngineHealth 熔断落盘、SearchCache、WebAddressGuard（钉 IP 防 SSRF）、设置/治理/场景归属。上一版方案里未兑现的部分并入下面。

- [x] **W1 [P1] 搜索预算被工具超时截断** — 已完成
  - 证据：`WebOptions.cs:26` 允许预算到 60s，`SettingsValidation.cs:161` 照此校验；但工具统一 `ToolTimeout=30s`（`AgentTurnRunner.cs:63,705-714`）。
  - 问题：预算设到 30s 以上时，搜索在 30s 被取消，已到的结果全丢，只报 `timeout after 30s`。
  - 方案：工具支持按名覆盖超时（`web_search` 取预算 + 5s），或把预算 clamp 到 `ToolTimeout - 2s` 并在设置页提示；Fanout 在取消前返回已到结果。
  - 验证：预算 60s + 假传输 35s 延迟，断言返回部分结果而不是 timeout。

- [x] **W2 [P1] 返回体积无上限**：`ResultFormatter.cs:37` 只按条数截断，snippet 原样输出（`:79-82`），arXiv/Wikipedia 摘要不截（`ArxivEngine.cs:133`、`WikipediaEngine.cs:100`）。方案：单条 snippet ≤ 300 字 + 总字符预算；`web_fetch` 同样按预算分段返回。验证：回放 fixture 断言输出长度上限。 — 已完成（见 §0 第三批备注）

- [ ] **W3 [P2] 熔断落盘在热路径**：`EngineHealth.Record` 每次变更同步 `Save()`（`EngineHealth.cs:195-198,235-267`），一次搜索最多 7 次原子写。改为标脏、搜索结束合并写一次。验证：7 引擎全失败时 `File.Replace` 计数为 1。

- [ ] **W4 [P2] HostGate 固定 250ms/主机**（`HostGate.cs:19,55-104`），覆盖每个重定向跳与 `web_fetch`，等待还不计入 `EngineTimeout`（`WebTransport.cs:63,148`）。抓取路径缩短间隔或首抓豁免；等待计入超时。验证：端到端两页抓取耗时。

- [ ] **W5 [P2] Data 请求重定向后变成浏览器头**：`WebTransport.cs:345-353` Data 分支提前 return 未写 `KindKey`（`:372`），重定向时默认 Document（`:190-193`）。Data 分支也写 KindKey。验证：Data 302 后第二跳 UA 断言。

- [ ] **W6 [P2] 补齐上一版方案未做的测试与工具**：`WebFanoutTests`（首结果后计时、跳过节流、善后窗口）；重定向逐跳 SSRF 测试；离线回放评测与诱饵审计（`BatchGate.cs:45-49` 阈值仍是暂定值，fixtures 需改为真实抓取）；`disabledEngines` 校验未知 id（`SettingsValidation.cs:145-149`）。

- [ ] **W7 [P3] 清理与小优化**：`ExemptFromHostFilter`（`WebModels.cs:243`）只声明未读——实现通用引擎主机名过滤或删除；删死代码 `SearchQuery.Words`（`:178`）、`RateLimit.Skip`（`:260`）；Merger 逐对比较且重复分词（`Merger.cs:50-61,95-103,286`），预计算 URL 归一与 token 集；缓存 key 含 limit（`SearchCache.cs:41`），改为按最大 limit 缓存再切片。

---

## 4. 主 / 子 agent 委派

已落地：3 个封闭场景与能力唯一持有者（`AgentScenarios.cs:30-47`、`AgentRouting.cs:33-70`）、`agent_delegate` 立即返回、同目标 10 分钟防重、JobEngine 轮次判定、`task_report` 强证据、journal 启动恢复、取消、UI 卡片轮询。默认路径未见必坏项。

- [x] **A1 [P1] 同一子 agent 不能排队**：`SubAgentHost.cs:87-91` 一个 agent 同时只允许一个 job，第二次委派直接 busy 拒绝。方案：每 agent 有界队列（默认 2，可配置），排队时立即返回 jobId 与"排在第 N 位"。验证：连续两次委派，两份汇报都到达且顺序正确。 — 已完成（见 §0 第三批备注）

- [x] **A2 [P1] web 子 agent 步数预算只有 8**：子 agent 没设 `ToolStepBudgets`（`SubAgentHost.Run.cs:51-57`），默认 `MaxSteps=8`，前缀表只有 desktop/mcp_/file_（`AgentTurnRunner.cs:14,22-23`）。方案：加 `["web_"] = 24`，并允许按场景配置。验证：脚本化 5 次 `web_fetch` 后仍能 `task_report`。 — 已完成

- [x] **A3 [P1] 单轮超时留下孤儿回合** — 已完成
  - 证据：`JobEngine.cs:160-177` `RunBoundedAsync` 超时后只放弃等待，不取消该轮 token；被放弃的回合继续调模型和工具、写同一个子会话，与下一轮并发。
  - 方案：每轮建专用 linked CTS，超时先 Cancel，再限时等待其退出；仍不退出则标记该会话为脏、终止 job，而不是开下一轮。
  - 验证：注入永不返回的回合，断言取消被观察且没有并发写入的事件。

- [x] **A4 [P1] 回传主 agent 无界**：`JobOutcome.ToMessage` 原样拼 Summary/Evidence（`JobOutcome.cs:64-92`），只有 Error 截 400 字（`JobEngine.cs:38`）。方案：Summary ≤ 1.5k、Evidence ≤ 2k 字，超出部分给 jobId 指向 `/api/agent-jobs/{id}/session`（`AgentEndpoints.cs:59-86`），主 agent 需要时用工具读取。验证：5k evidence 断言截断 + 指引。 — 已完成（见 §0 第三批备注）

- [x] **A5 [P1] 交给子 agent 的上下文过薄**：`agent_delegate` 只有 goal/context 两个字符串（`DelegateTools.cs:29-35`），`FirstMessage` 直接拼（`JobEngine.cs:186-189`）。方案：默认附带用户原话与最近 1-2 轮摘要（限长），可选附件路径列表。验证："把刚才那个文件读完"这类指代能在子会话里解析。 — 已完成（见 §0 第三批备注）

- [ ] **A6 [P2] 运行中没有过程流**：子回合 `stream: null`（`SubAgentHost.Run.cs:76`），状态只有轮数（`DelegateTools.cs:87-103`，`AgentJob.cs:104-118`）。方案：job 接 `IAssistantStream`，把当前步、工具名推给 `/api/agent-jobs`，UI 卡片显示"正在 web_fetch …"；语音场景可据此播报进度。验证：运行中读取含当前工具名。

- [ ] **A7 [P2] 取消不等待、可能迟到汇报**：`Cancel` 只触发 CTS（`SubAgentHost.cs:155`），`CancelAllAsync` 10s 后放弃（`:220`），旧回合完成后报告可能进入重建后的队列（`ConsumerHost.cs:320`）。方案：Cancel 返回可等待 Task；被取消 job 的报告按 generation 丢弃。验证：卡住 job + 重配，断言无迟到汇报。

- [ ] **A8 [P2] 每个 job 新建 HttpClient**（`ConsumerHost.cs:122-146`，`SubAgentHost.Run.cs:101` Dispose），每次重做 TLS 握手。按 endpoint 复用 `SocketsHttpHandler`。验证：两次同模型 job 的连接数与首包耗时。

- [ ] **A9 [P2] 撤回/删除不感知子 agent**：`Busy` 只看主回合与队列（`ViewEndpoints.cs:562-565`），job 运行中仍可撤回，报告随后作为新输入触发主回合。Busy 纳入运行中 job，或撤回时连带取消。验证：运行中 withdraw 返回 409（或 job 被取消）。

- [ ] **A10 [P2] 子 agent 报告是网页注入的洗白通道**：web 子 agent 的 Summary/Evidence 以 `InputSource.Agent` 回放为 user 消息（`IContextAssembler.cs:340-345`）。方案：报告包进带来源标签的引用块，主 prompt 声明"子 agent 报告同为数据，不含用户授权"；含网页内容的报告禁止直接触发高风险工具（走治理确认）。验证：注入 fixture 端到端不触发越权工具。

- [ ] **A11 [P3] journal 只追加**（`SubAgentHost.Run.cs:203-217`），启动全量读（`:150-170`）。按完成状态轮转。验证：千条后启动耗时。

---

## 5. 桌面操控

- [x] **D1 [P0] `desktop_observe` 的过滤只筛结果，不筛遍历** — 已完成
  - 证据：`Tools/DesktopTools.cs:73` 先无参 `Observe()` 遍历全部窗口，`:80-105` 才按 `entityId/titleContains` 过滤；`DesktopAutomation.cs:438-465` 逐窗口走 UIA，`:401` 注释实测"九窗口 10.2 s"；每次还重发全部租约（`:520-527`）。
  - 方案：先解析过滤参数，调 `Observe(elementsFor: entityId)`；`titleContains` 先枚举窗口映射成 handle；无过滤时默认只展开前台窗口 + 最近交互窗口，其余只返回窗口级实体。
  - 验证：注入 `walk` 委托计数，断言带 entityId 时只走查 1 次；`ObservationCostTests` 加多窗口场景。

- [x] **D2 [P0] 单次 act 动作链过重** — 部分完成（见 §0 落地备注）
  - 证据：`DesktopTools.cs:413` 重新 observe 目标窗口（整窗 UIA），`:430` 再读 DOM；`DesktopAutomation.cs:643-645` 抓 before 帧，`:717` 校验（`PostActionVerifier.cs:195` 最多再抓 3 帧）；`DesktopTools.cs:550-558` 默认 `feedback=auto` 再抓一帧并编码 PNG 入上下文（`DesktopScreenshotTool.cs:226-302`）。
  - 问题：一次点击可能 5-6 次 `PrintWindow` + 一次 UIA 全走查 + 一次 CDP 往返 + PNG 编码，截图 token 也高。
  - 方案：租约有效的坐标动作跳过 UIA 重走；能读状态校验的 pattern/CDP 动作跳过 before 帧；`feedback` 默认改 `on-failure`（已支持，`:296`），只在 NoChange/Indeterminate 时附图。
  - 验证：stub `IScreenCapture` 计数，断言一次成功点击的抓帧数下降；对比 `/api/desktop/telemetry` 的 `actP50Ms`。

- [ ] **D3 [P1] 校验只有像素差分**：`DesktopAutomation.cs:767-775` pattern 动作按整窗比较；`PostActionVerifier.cs:44-58` 预算 100ms、阈值 0.005，`:398-405` 单线程逐行扫。4K 整窗常超时返回 Indeterminate，小控件变化比例不足被误判 NoChange。方案：pattern 动作读 `Toggle/SelectionItem/Value` 状态；CDP 动作读 DOM 状态（`CdpDomSource.cs:248` 的 `EvaluateAsync`）；像素差分改按 32 位字比较 + 分块早退，预算按面积自适应。验证：4K 帧性能断言；Toggle 后返回 AnyChange。

- [ ] **D4 [P1] elementId 是位置下标，不稳定**：`UiaTree.cs:11,313-323` 按遍历顺序编号，`DesktopEntities.cs:162` 身份只有 window+name+role，`RevalidateElement`（`DesktopTools.cs:685-700`）区分不了同名同角色控件。方案：id 改用 UIA `RuntimeId`（CDP 已有 marker），名称只作展示。验证：`WalkTruncationTests` 加"插入一个控件后旧 id 不再误命中"。

- [ ] **D5 [P1] CDP 能力缺口**：`CdpDomSource.cs:67-147` 只 `querySelectorAll`，不穿 shadow DOM、不进 iframe；没有 `Page.captureScreenshot`/`Page.navigate`；`CdpRegistry.cs:9-21,50-53` 只登记本进程启动的端口，`Forget`（`:56`）无调用点。方案：递归 shadowRoot + iframe；`Accessibility.getFullAXTree` 作为结构化来源；后台窗口用 `Page.captureScreenshot`；可选接入用户已开的调试端口；端口存活巡检调用 `Forget`。验证：`CdpDomTests` 与 `CdpDomLiveTests` 加 shadow/iframe 用例。

- [x] **D6 [P1] CDP 每次动作重复探测**：`CdpDomSource.cs:285,337,575` 每次都请求 `/json/list`；`CdpRegistry.cs:98` 每次轮询 `new HttpClient`（最多 30 个）。方案：页面列表按端口缓存 1-2s；共享静态 `HttpClient`。验证：注入 connect 计数，断言一次 act 只探测一次。 — 已完成（见 §0 第三批备注）

- [x] **D7 [P1] observe 返回体积无上限**：`DesktopTools.cs:116-160` 序列化全部窗口全部元素，每窗口 40 个（`UiaTree.cs:54-55`），value 最长 200 字（`:234`），每条带 clickAt。方案：加 `maxElements`/`roles`/`nameContains` 与分页游标；默认只返回前台窗口元素；有 elementId 时省略 clickAt。验证：`DesktopScopeTests` 加元素数与字符数上限断言。 — 已完成（见 §0 第三批备注）

- [x] **D8 [P1] 截图 token 与编码成本**：默认 1280（`DesktopScreenshotTool.cs:31`），全帧 PNG（`:530-566`），标记清单最多 24 条且与图上数字重复（`:502-513`）。方案：JPEG/WebP 可选；标记文本只列 name + elementId；动作后截图裁到变化区域；`maxDimension` 随剩余上下文自适应。验证：媒体字节数上限断言；`ScreenshotMarksTests` 断言格式。 — 已完成（见 §0 第三批备注）

- [ ] **D9 [P2] 观察对象被并发原地修改**：`DesktopEntities.cs:127` `Elements` 可写，`DesktopAutomation.cs:884-887` 无锁替换 `_lastObservation.Elements`，而工具允许并行调用（`:180-183`）。改为每次生成不可变新 observation（`with`），读取方取局部引用。验证：`ObservationConcurrencyTests` 加并行 observe + AddDomElements。

- [ ] **D10 [P2] `desktop_wait` 每 250ms 一次全量 observe**（`DesktopWaitTool.cs:99-130`），5s 内最多 20 次全桌面枚举与租约签发。新增只走单窗口、不签租约的轻量路径，间隔指数退避。验证：5s 内走查 ≤ 3 次。

- [ ] **D11 [P2] 采集后端**：只用 GDI（`Interop/NativeMethods.cs:9-13`），`GdiScreenCapture.cs:75-111` 每次 `PrintWindow` 整窗重渲染，失败回退 BitBlt（被遮挡时无效）；GPU 应用可能空白。方案：加 Windows.Graphics.Capture / DXGI Desktop Duplication，保留 GDI 回退；混合 DPI 多屏加运行时坐标校验（`DpiAwareness.cs:44` 只在启动调用）。验证：`PARTNER_DESKTOP_LIVE=1` 下 150% DPI 双屏 live 测试。

- [ ] **D12 [P2] 能力面补齐**：drag 支持 `elementId → toElementId`（现只收坐标，`DesktopTools.cs:727-741`）；act 支持 `target:{name, role}`，歧义时拒绝；新增 `desktop_window`（还原/最大化/最小化/移动）；结果加结构化 `errorCode`（transient/terminal，对接 `JobEngine.Facts.cs` 前缀协议）与 `verify` 开关（现为纯文本，`DesktopTools.cs:986-1027`）。验证：`DesktopActGuardTests` 加按名定位与歧义拒绝。

- [ ] **D13 [P3]**：`_actionLatenciesMs` 无界增长（`DesktopAutomation.cs:170,723`），改环形缓冲；`DesktopTelemetry.cs:47` 只在内存保留 1 小时，按需落盘聚合。

---

## 6. 语音输入 / 输出

- [x] **V1 [P0] 首音要等整段回复生成并全部合成完** — 已完成
  - 证据：`VoiceOutputHalf.cs:137` 先 `_sink.Hold()`，`:151` 等 `PlayPendingAsync` 把所有句子合成进 sink，`:153` 才 `Release()`；`VoiceHost.cs:460-472` 只在读到 `SessionEventKind.Assistant` 后 FlushTurn，而该事件在模型整段返回后才写（`AgentTurnRunner.cs:355`）；`VoiceHost.cs:209` 还有 200ms 轮询。
  - 问题：首音 = 模型全文生成 + 轮询 + 全部句子合成（首句 RTF 1.23，见 `engines/README.md`），长回复是十几秒静音。Hold 的本意是消除句间静音，但代价落在首句上。
  - 方案：RunOutputAsync 订阅已有的流式 delta（`IAssistantStream.OnAssistantDelta`，`ConsumerHost.cs:224`），增量 Push 进 Segmenter；第一句合成完就 Release，后续句子保持"合成领先播放 1-2 句"的流水线，句间无缝的目标不变；回合结束时 Flush 尾句并按已播文本去重。
  - 验证：打点"首个 delta → 首次出声"，目标 < 1.2s；扩 `VoiceHostTests.cs` 断言首句在回合结束前开始播放、句间无空洞。

- [x] **V2 [P1] 没有 barge-in，播放期间用户说话全部丢弃**：`VoiceInputHalf.cs:193-204` 闸门内直接 return，只累计 `_skippedHadSpeech`；`DetectedSpeechWhileGated`（`:118`）除 smoke 外无人消费；闸门只在 `VoiceOutputHalf.cs:157` 上。方案：播放期继续跑能量检测 + KWS（复用 `:390` 的回灌屏蔽窗），命中唤醒词或持续语音即 `_sink.Stop()`、清空未播 TTS 队列、放闸；记录"被打断时已播到哪一句"写入会话，供下回合上下文使用。验证：smoke 注入"播放中说话"，断言 sink 停止、utterance 入队。 — 已完成（见 §0 第三批备注）

- [x] **V3 [P1] ASR 阻塞 tick，流式 ASR 未接线**：`VoiceInputHalf.cs:152-157` 每 tick 顺序执行，`:506` await `TranscribeAsync`（最长 3s，`AsrOptions.cs:42`），期间端点判定与唤醒探针停摆；`HttpAsrClient.TranscribeStreamAsync`（`HttpAsrClient.cs:58`）零调用者。方案：ASR 提交改后台 Task，tick 继续 Step（复用 `SubmissionOutcome.Busy`/`_heldPause`）；接 interim 结果给 UI；长期换流式模型。验证：连读 5 句时 tick 间隔 p99 < 50ms。 — 已完成（见 §0 第三批备注）

- [ ] **V4 [P2] 唤醒延迟 320-440ms**：`VoiceInputHalf.cs:268-302` 攒满 0.1s 才发、每 100ms 一次同步 HTTP；`:418`、`:432` 的 lead/catch-up 是为此打的补丁；引擎已有 `/wake?seq=` 长轮询（`engines/kws-asr/asr-server.py:76-86`）宿主未用。方案：增量降到 50ms，宿主改长轮询，优先 chunk-8 模型。验证：喊词 → `_awake=true` < 250ms。

- [ ] **V5 [P2] 端点判定只有固定 RMS 阈值**：`PauseBlockingDetector.cs:163` 固定 `SilenceThreshold`，底噪误配只做诊断（`VoiceInputHalf.cs:166-190`）。方案：滑动最小值估计底噪 × 系数自适应；可选 Silero VAD 只用于端点判定。验证：安静/噪声两套 30s 录音的误切与漏切计数。

- [ ] **V6 [P2] 引擎常驻与冷启动**：`asr-server.py:97-113` 启动即建 SenseVoice（idle 私有内存 682MB），`tts-server.py:107-138` 无预热。方案：recognizer 首次请求懒建（或可配置）；TTS 启动后合成一句丢弃的短句预热；宿主 ready 后主动 ping。验证：idle 内存与首句延迟对比。

- [ ] **V7 [P3] 失效配置与死代码**：`VoiceOutputHalf.cs:66` `new SpeechSegmenter()` 忽略 `MaxSplitChars`；`GapMs`（`VoiceSettings.cs:69`）无消费者；`VoiceHost.cs:477` `pendingBlock: false` 硬编码；`:470-471` 连调两次 PlayPending；`VoiceOutputHalf.cs:203` `emitted ? Played : Played`。逐项接线或删除，避免设置页项不生效。

---

## 7. 定时任务

- [x] **S1 [P0] 失败任务 50ms 无限重试** — 已完成
  - 证据：`JsonlScheduler.cs:255` due 集只排除 `Inactive`，`Failed` 仍在；无未来时间时等 `MinWait`=50ms（`:384-394`）；每次失败 `Attempt++` 并落盘（`:419-432`），任务与回执各一次 `Flush(true)`（`:438-449`，`FileAppendOnlySink.cs:70`）。
  - 问题：队列持续失败时每秒约 20 次 fsync、20 条快照，`Attempt` 无上限。
  - 方案：任务加 `NextRetryAt`，指数退避 1s → 5min 封顶，超过 N 次转 `Failed` 并移出自动 due 集（等手动 resume 或下一个自然触发点）。
  - 验证：`JsonlSchedulerTests` 加永久失败队列，10s 内尝试 < 10 次。

- [x] **S2 [P1] 投递全程持锁且入队可阻塞**：`JsonlScheduler.cs:248-322` 整个 pass 在 `_gate` 内，`:282-288` 调 `EnqueueAsync`，队列满时等 `_slots.WaitAsync`（`JsonlInputQueue.cs:160`）；`ListAsync`（`:224-242`）与 `/api/schedules` 同锁。方案：锁内只取 due 快照，锁外投递再回锁写结果；`EnqueueAsync` 加超时，超时进退避。验证：满队列下 `GET /api/schedules` p99 < 100ms。 — 已完成

- [x] **S3 [P1] 到点后没有通知，也看不到执行结果**：只 `EnqueueAsync(InputSource.Schedule)`（`:282-288`），执行在 `TurnLoop.ProcessAsync`（`TurnLoop.cs:85-149`），失败只标 queue item；`ScheduleTools.cs:153-158` 的 `LastError` 只反映投递错误。方案：回合完成/失败按 itemId 回写任务的 `LastRunResult/LastError`；UI 接入已有 ToastHost 弹通知；语音开启时可选播报结果。验证：模型返回 500 时页面出现失败提示、lastError 更新。 — 已完成（见 §0 第三批备注）

- [x] **S4 [P1] 错过的运行只补一次，无策略**：`JsonlScheduler.cs:397-405` 跳过所有过去时刻（`JsonlSchedulerTests.cs:293` 固化"睡 1 小时只补 1 次"）；应用不运行就不触发（`ApiContext.StartScheduler`，`:87`）。方案：每任务 `catchUp: skip | once | all(cap)`，过期超过宽限期的标记为"已错过"并显示；可选注册 Windows 计划任务在开机时拉起应用。验证：时钟前跳 3 天，按三种策略断言投递次数。 — 已完成（见 §0 第三批备注）

- [ ] **S5 [P2] 日志只追加不压缩**：每次变更写整条快照（`:438`），启动全量重放（`:472-498`），`FileAppendOnlySink.Rewrite`（`:164`）未用。方案：行数超阈值时 Rewrite 成每任务一条；回执按保留窗裁剪。验证：模拟 1 万次投递后的文件行数与启动耗时。

- [ ] **S6 [P2] 定时消息与用户输入不可区分**：`AgentTurnRunner.cs:293` 写成 `SessionEventKind.User`（Source=Schedule），`SessionEventKind.Schedule`（`SessionEventKind.cs:21`）全仓无写入点，装配器却会读（`IContextAssembler.cs:246,352`）。方案：改写 `Schedule` 事件，并在该回合提示里说明"由定时任务触发，用户可能不在场，不要追问，直接完成并汇报"。验证：会话日志 kind 与模型可见前缀。

- [ ] **S7 [P3]**：`ScheduleRules.cs:48-60` 先 List 再 Schedule，并发创建可越过 100 上限（移入锁内判断）；时区固定启动时的 `TimeZoneInfo.Local`（`ApiContext.cs:177-178`），监听系统时区变更；`CronExpression.cs:45-50` 只支持 5 段，按需补 `L/W/#`；DST 回拨行为写进文档。补可观测指标 `LastDeliveryAt - occurrence`（调度精度本身够用，延迟主要来自排队等回合）。

---

## 8. 跨模块共性

- [ ] **X1 [P1] 统一的大结果预算**：C1、W2、A4、D7、D8 都是"工具结果无上限进上下文"。抽一个 `ToolResultBudget`（按剩余上下文比例给每次工具调用配额，超出时截断并返回续读句柄），各工具共用，避免各自定阈值。
- [ ] **X2 [P2] 统一延迟打点**：首音（V1）、唤醒（V4）、act（D2）、搜索（W1）、委派（A6）各自打点后汇总到一个诊断页，作为后续优化的基线；优化前先记录一次当前值。
- [ ] **X3 [P2] HttpClient 复用**：A8、D6 与 Web 传输各自建客户端，统一走 `IHttpClientFactory` 或共享 handler。

---

## 9. 未完成清单（截至 39c6e4d）

做到哪一步以 §0 的三批"落地备注"为准；这里只列还没做的，按"值得先做的程度"排。已完成的条目在正文里都已打勾。

### 9.1 P1 里整条没做

- [ ] **D3 校验只有像素差分**：pattern 动作读 Toggle / SelectionItem / Value 状态，CDP 动作读 DOM 状态，像素差分改 32 位字比较 + 分块早退、预算按面积自适应。做完它，D2 里"能读状态校验的动作跳过 before 帧"才有条件做。需要真实桌面验证（4K 帧、Toggle 前后）。
- [ ] **D4 elementId 是位置下标**：id 改用 UIA `RuntimeId`（CDP 已有 marker），名称只作展示；`WalkTruncationTests` 加"插入一个控件后旧 id 不再误命中"。
- [ ] **D5 CDP 能力缺口**：递归 shadowRoot 与 iframe；`Accessibility.getFullAXTree` 作结构化来源；后台窗口用 `Page.captureScreenshot`；可选接入用户已开的调试端口；端口存活巡检里调用 `CdpRegistry.Forget`（D6 里没做的那一项）。需要真实浏览器验证。
- [ ] **X1 统一的大结果预算**：现在只有 C1 的 `ToolResultBudget`（回合内），W2 / A4 / D7 / D8 各自定了上限。要不要抽成"按剩余上下文比例给每次工具调用配额"，看实际还有没有漏网的大结果再决定。

### 9.2 已做条目里留下的缺口

- [ ] **C2** 缺"超预算后跑一回合，出现 Summary + Retract，下一回合 `FoldedItems == 0`"的端到端用例（目前只测了回调时序和失败隔离）。
- [ ] **C4** 摘要里的稳定事实同步写进 Semantic 记忆。
- [ ] **C5** `HttpLlmClient` 的 `cache_control` 断点；响应里的 cached tokens 记录。需要先确认实际用的服务商是否支持。召回块改放在最后一条 user 消息前面后，要在真实模型上看一下行为有没有变化。
- [ ] **M6** 写入前按内容哈希去重（要先改 `MemoryLifecycle` 里"完全相同不算冲突"的约定）。
- [ ] **M7** 注入的 MMR 多样性。
- [ ] **A2** web 子 agent 步数预算"按场景配置"（现在是全局默认 `web_` = 24）。
- [ ] **A5** 委派时附带附件路径列表。
- [ ] **D1 / D2** 真机上量一次 `actP50Ms` 和一次 observe 的耗时，和优化前对比（`/api/desktop/telemetry`）。
- [ ] **D7** observe 的翻页游标；有 elementId 时省略 `clickAt`。
- [ ] **D8** JPEG / WebP 可选；动作后截图裁到变化区域；`maxDimension` 随剩余上下文自适应。需要在真机上看画质与 token 的取舍。
- [ ] **V1** "首个 delta → 首次出声 < 1.2s" 没在真机上测过（依赖真实 TTS 的 RTF）；真 TTS 下句间是否仍有空洞也没听过。
- [ ] **V2** 设置页里的"允许打断"开关（现在要改 `runtime.json`）；用唤醒词（KWS）作为更稳的打断信号；把"被打断时已播到哪一句"写进会话。
- [ ] **V3** ASR interim 结果给 UI；换流式模型。
- [ ] **S3** 到点任务的结果在界面里弹 Toast；语音开启时可选播报结果。
- [ ] **S4** 设置页 / 定时任务页里的补跑策略选项和"已错过"展示；Windows 计划任务在开机时拉起应用。
- [ ] **S1** 方案里写的"超过 N 次转 Failed 并移出自动 due 集"没做（有测试固化了"Failed 仍可调度"），如果要改要先改设计文档 §2.4。

### 9.3 P2 整条没动

- [ ] 上下文：**C6**（CJK 估算已部分覆盖；provider 计数并行 / 批量、用 `usage.prompt_tokens` 校准、工具 schema 计入预算）
- [ ] 记忆：**M8** 衰减与向量、**M9** `MemoryStore` 并发安全
- [ ] 搜索：**W3** 熔断落盘合并写、**W4** HostGate 250ms、**W5** Data 请求重定向后的头、**W6** 补齐上一版方案未做的测试与工具
- [ ] 委派：**A6** 运行中的过程流、**A7** 取消不等待 / 迟到汇报、**A8** 每个 job 新建 HttpClient、**A9** 撤回 / 删除不感知子 agent、**A10** 子 agent 报告是网页注入的洗白通道
- [ ] 桌面：**D9** 观察对象被并发原地修改、**D10** `desktop_wait` 全量 observe、**D11** 采集后端、**D12** 能力面补齐
- [ ] 语音：**V4** 唤醒延迟、**V5** 端点判定自适应阈值、**V6** 引擎常驻与冷启动
- [ ] 定时任务：**S5** 日志只追加不压缩、**S6** 定时消息与用户输入不可区分
- [ ] 共性：**X2** 统一延迟打点、**X3** HttpClient 复用

建议先挑：**A10**（安全相关，网页内容经子 agent 报告进主 agent）、**A7 / A9**（取消与撤回的正确性）、**S6**（定时消息提示不到位，模型会向不在场的用户追问）、**M9**（并发安全）。

### 9.4 P3 整条没动

**C7**、**M10**、**W7**、**A11**、**D13**、**V7**、**S7**，见各自章节。

### 9.5 已知的小问题

- `ConsumerEndpointTests.Cancelling_a_queued_item_keeps_it_out_of_the_turn` 在全量并行时偶发失败一次（单跑与重跑都过），是测试里的时序假设，没追。
- 三批改动没有在带界面的预览里验证过（本机没起 UI）；涉及界面的项（V2、S3、S4）只做了后端与工具层。
