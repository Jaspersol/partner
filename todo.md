# Todo — 记忆/桌面/核心能力优化(2026-10-13 全仓分析)

基准:2026-10-13 状态——全 solution 983 单测绿 / smoke SMOKE OK / GATE PASS 65 能力。上轮桌面优化(O 遥测、F1、C1、C2、S1–S4)已收尾,工作树改动待提交。

本轮来源:记忆(M5)、桌面(M9)、核心(M1–M4/M7/M8)三路全仓探查。优先级:**先修生产接线缺位(记忆多数弱点是"功能在、没接上"),再修稳健性,调参仍等遥测数字**。

每期收尾不变:smoke `Check(...)` + `Capabilities` 表 → `--update-baseline capabilities/manifest.baseline.json` → README 补节 → 跑 smoke 前停应用(坑 11/12)。

---

## M — 记忆能力(最大缺口:生产接线缺位)

### M0 接线缺位(功能已实现但生产未用——最高优先,全是小改动)

- [x] **M0.1 回忆注入走 token 预算路径**(2026-10-13)
  做法:`MemoryRecallHook` 改走 `IMemoryStore.InjectAsync`(生产预算即默认 5000),首次放不下即带截断标记;`recallLimit` 参数顺带退役。验收:单测超预算注入带标记(`Injection_stops_at_the_budget_and_says_so`);smoke `memory.injection_budget` 现断言 store 侧 + 生产钩子侧两_paths。

- [x] **M0.2 生命周期维护挂调度器,不再寄居读写路径**(2026-10-13)
  做法:`Api/MemoryMaintenanceService`(TimeProvider 注入,默认每日)随 `ApiHost` 起停,单遍 `PromoteEligible → DemoteIdle(已公开到 IMemoryStore)→ Trim → Compact`,失败只记 stderr 下次重试。无生产调度器实例(JsonlScheduler 仅 smoke),故用定时器而非调度器——与 todo 的「倾向调度器」取舍为**宿主生命周期对齐**(调度器会话与 queue 绑定,维护不该有自己的输出口)。验收:Core 单测「90 天未召回 + TTL 过期」例程后降级/清除(episodic 副本无 TTL 幸存);Api 单测跑真实工作区 store 计数回执;smoke `memory.maintenance_pass`。

- [x] **M0.3 嵌入 provider 接线(或如实声明 FTS-only)**(2026-10-13)
  做法:settings 增 `memory.embeddings` 段(默认关),开时 `ApiContext.OpenAsync` 接 `HashingEmbeddingProvider`;**启动时读一次**——中途改开关不动已开的库(不会让新记录悄悄带上旧记录没有的向量),重启生效即文档语义。模型端 embedding 端点仍不做(外部依赖)。验收:单测开=vector 分量非零(`Memory_embeddings_saved_to_settings_take_effect_at_the_next_open`);关=行为不变(默认路径未传入 provider);README 写默认 FTS-only。

- [x] **M0.4 无模型时缩窗保存不再抛异常**(2026-10-13)
  现状核实:`ConsumerHost.CompactAsync` 无 llm 时**已不抛**(返 `CompactionResult(0, null, "未配置模型，无法压缩")`),todo 描述的 500 已不存在——本轮只补验证与兜底确认。验收:Api 单测无 llm + 缩窗保存 → 200 + `compaction.compacted=false` + reason,历史不丢(`Shrinking_the_context_folds_the_history_once`);smoke 无模型宿主外加同形状检查 `settings.no_model_shrink_saves`。补压(「下次回合开始或调度器例程补压」)未做:compaction 是一次性补叠,搁在保存时机的决定已由 no-llm 报告承担,先不加第二条触发路径。

### M1 检索质量

- [ ] **M1.1 CJK 真分词(替换 bigram LIKE OR 全表扫描)**
  现状:unicode61 把整句 CJK 当单 token,FTS 服务不了换词问句;粗筛退化为 200 条 bigram 的 OR LIKE 扫描(`MemoryStore.cs:776-836`),成本随库增长,`memory.scale_p99` 基线只测了 Latin FTS 路径。
  改法:FTS5 自定义分词不可行(SQLite 编译选项)则改双写:CJK 内容额外存 bigram 表走 FTS5 查询(把 LIKE OR 变成 FTS 索引查询);`CandidateLimit` 保留为兜底。
  验收:CJK 换词问句(共享词 <2)召回非空;10 万条 CJK 库 P99 <500ms(补 `memory.scale_p99_cjk` 或并入现有检查)。

- [ ] **M1.2 `FindBySourceAsync` 建索引**
  现状:`source_event_seq` 无索引(`IMemoryStore.cs:79-85` 注释自认);会话级联删除对区间逐 seq 查询,千 seq 撤回 = 千次查询。
  改法:`memories(source_event_seq)` 建索引 + 区间批量查(一条 `WHERE source_event_seq BETWEEN`);schema 迁移走试跑 ALTER 路径(见 M2.1)。
  验收:单测千 seq 区间级联一次查询(可用计数器断言 SQL 次数)。

### M2 数据层稳健性

- [ ] **M2.1 schema 版本号 + 显式迁移**
  现状:无版本号,迁移靠逐条 ALTER 吞异常(`MemorySchema.cs:34-49`);未来改列类型/拆表无框架。
  改法:`PRAGMA user_version` 打版本,版本表驱动迁移步骤;现有"试跑吞异常"归为 v0→v1 的兼容步骤。
  验收:单测旧库(缺列)打开后自动迁到 v1 且功能不破。

- [ ] **M2.2 `ParseScope` 未知值显式记录**
  现状:枚举缩容遗留的未知 scope 静默当 `Recall`(`MemoryStore.cs:849-850`),数据语义漂移无感知。
  改法:未知值记入结果附注(或遥测),行为保持兜底不破。
  验收:单测未知 scope 值 + 断言附注。

### M3 蒸馏与覆盖面

- [x] **M3.1 工具型回合也产记忆**(2026-10-13,与 M0 同批)
  做法:`Core/Memory/ToolAppearance.Summarize`——无 assistant 文本的回合取本回合内最长一条 `desktop_act` 成功 ToolResult(ok=true 且带「已通过界面自动化执行 / 注入成功 / 已按控件中心点执行」标记),其余一律不收;拒绝、校验失败、`ok=false` 全排除。验收:单测成功 act 产 Working 记录(`A_tool_only_turn_is_remembered_from_a_successful_act`)、纯失败回合不产(`A_all_failed_tool_turn_leaves_no_memory`)。

- [ ] **M3.2 蒸馏词表外置可扩充**(低优先,M3.1 后再看)
  现状:SelfReferential/Chitchat/importance 词表硬编码中英短语(`ConversationDistiller.cs:46-60, 128-138`),"别忘了"/"note that" 等漏网;`MaxBodyLength=400` 截断不挑句界。
  改法:词表外置到设置或数据文件;截断对齐句子边界(句号/问号/换行回退)。
  验收:单测"别忘了"被蒸馏、"note that"命中 importance;截断点落在句界。

- [ ] **M3.3 矛盾检测阈值改密度**(低优先)
  现状:`MinSharedKeywords=5` 绝对计数(`MemoryLifecycle.cs:32`),超短句共享词不足漏掉 supersede,新旧事实并存。
  改法:改密度比(交集/较短句关键词数 ≥ 阈值),短句不再漏;阈值拿现有单测数据回归调。
  验收:单测"换用 D 盘了"类短句能 supersede;既有矛盾用例不回归。

### 不做(记忆)

- 记忆库导出/备份 UI:存储立场是"删除即丢失、库即权威",先不做;README 已明示。
- 语义 embedding 服务默认接入:依赖外部端点,留在 M0.3 的可选项,不默认开。

---

## D — 桌面操控(T 区数字未到前,只修结构性弱点)

### D0 遥测兑现(先收上轮埋点的数,决定 T 区)

- [ ] **D0.1 跑一轮真实会话,读 `GET /api/desktop/telemetry`,按结果裁定 T 区四项**
  snap 命中率 <90% → 吸附半径按控件尺寸归一化;zoom 用得多且命中低 → `zoomAt` 参数化;附图 token 占比 >25% → feedback 默认挡换 `on-failure`;租约耗尽率 >30% → 租约 10 步调参。
  **数字不到,四项都不动**(上轮原则延续)。
  产出:数字记进本文件 T 区条目,做出/不做结论。

### D1 稳健性小修(不依赖遥测)

- [ ] **D1.1 遥测 windowHours 与 sink 保留期联动**
  现状:聚合端点硬编码 1h(`DesktopEndpoints.cs:72`),sink 保留 1h——改其一即静默不一致。
  改法:聚合窗从 sink 的 Retention 派生,query 参数 clamp 到保留期内。
  验收:单测改保留期后端点窗一致。

- [ ] **D1.2 `desktop_exec` 后台子进程管道关闭**
  现状:30s 超时后管道可能不关(`DesktopSystemTools.cs:124-126` 自认),子进程与句柄滞留。
  改法:超时路径 kill 进程树 + dispose 管道;正常路径已关的不动。
  验收:单测假进程超时后无句柄滞留(Process.Exited + pipe disposed)。

- [ ] **D1.3 `_lastObservation` 单槽并发覆盖**
  现状:并发 observe 互相覆盖 walks/truncated(`DesktopAutomation.cs:185`,注释假设工具循环单线程);步预算 4 并行已在打破该假设。
  改法:walks/truncated 挪到 per-call 作用域或 Concurrent 注册;或埋点处直接落遥测不依赖槽。
  验收:单测并发两 observe,遥测两条各带自己的截断计数。

### 不做(桌面,上轮已判,防回流)

- attach 半(扫端口找已开调试目标)、WebView2 走 CDP、grounding 模型副路、OCR/沙箱/第二桌面/MCP 化桌面工具。
- UIPI fail-open 改 fail-closed:刻意设计,级别读不出的进程放行;改了会拒掉大量合法动作。
- region BitBlt 快路径竞态:窗口极小、代价是加锁;等真实误报再说。

---

## C — 核心能力(验证与补缺)

- [ ] **C.1 真实模型供应商往返验证**
  现状:`HttpLlmClient` 只验过线格式与假端点(FakeChatProvider/ScriptedLlmClient),OpenAI/Ollama 实跑未做——README"未做/未验证"表现在这么写。
  改法:一次性人工验证脚本(`scripts/`),对配置的真实端点跑一轮 chat + `/tokenize` + `/models`,结果记 README;**不进 smoke**(smoke 不依赖外部服务)。
  验收:README 未验证表删该项,记录实测供应商与版本。

- [ ] **C.2 ASR/TTS 真服务验证**(同 C.1 性质)
  现状:本机无服务,走脚本实现。
  改法:同上,人工验证一次,结果记 README。

- [ ] **C.3 README 坑 9 已过时清理**
  现状:README 写"无 .git",仓库已是 git(提交 537fd3a)。
  改法:删/改该条,顺检坑列表其余时效。
  验收:docs 引用检查过(`scripts/ci.sh` 首步)。

- [ ] **C.4 offscreen 控件挤占 40 配额缓解**(低优先)
  现状:长列表 offscreen 控件可把视野内按钮挤出产出配额,现缓解=先 scrollIntoView。
  改法:`UiaTree` 产出优先保留 viewport 相交控件,offscreen 按剩余配额截断;截断标记沿用 `ElementsTruncated`。
  验收:单测深列表场景视野内控件必在产出里。

### 不做(核心)

- slash-command dispatch(PreDispatch 无条件跑是既有设计,钩子可 ShortCircuit,无需求);
- 跨进程双消费者(队列进程内单消费者是结构立场,TFM 带 windows 后缀因语音须同进程);
- set-of-marks 编号徽章 / Phase 5c 帧级闭环:保持 README 未做表现状,无触发信号。

---

## 执行顺序建议

1. **M0.1–M0.4**(接线缺位,小改动大收益,一批做完一起收尾)**✅ 2026-10-13 完成**(同批顺带 M3.1)
2. **D0.1**(跑会话收数,裁定 T 区——不确定工期,尽早启动)
3. **M1.1 + M1.2**(检索质量,依赖 M2.1 的迁移框架先行)
4. **D1.1–D1.3 + C.3**(小修,可穿插)
5. **M2.x → M3.x → C.4**(数据层→覆盖面→低优先;M3.1 已随 M0 批完成)
6. **C.1/C.2**(人工验证,不占开发周期,有真实端点时顺手)

收尾清单(每期完成后勾):
- [x] smoke 新检查写进 `src/Partner.Smoke/Program.cs` + `Capabilities` 表(M0 批:4 条新检查、injection_budget 加 1)
- [x] `dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json`(2026-10-13 已刷新:163 检查 / 68 能力 / 66 product-supported;含上轮待提交的 desktop.telemetry_aggregates)
- [x] README 相应小节补行(含未做/未验证表)
- [x] 跑 smoke 前停应用(本轮连跑两遍均 SMOKE OK;px/py 检查为环境敏感,无应用干扰时通过)
