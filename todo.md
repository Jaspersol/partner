# Todo — 记忆/桌面/核心能力优化(2026-10-13 全仓分析)

基准:2026-10-13 状态——全 solution 983 单测绿 / smoke SMOKE OK / GATE PASS 65 能力。上轮桌面优化(O 遥测、F1、C1、C2、S1–S4)已收尾,工作树改动待提交。

本轮来源:记忆(M5)、桌面(M9)、核心(M1–M4/M7/M8)三路全仓探查。优先级:**先修生产接线缺位(记忆多数弱点是"功能在、没接上"),再修稳健性,调参仍等遥测数字**。

每期收尾不变:smoke `Check(...)` + `Capabilities` 表 → `--update-baseline capabilities/manifest.baseline.json` → README 补节 → 跑 smoke 前停应用(坑 11/12)。

---

## V — 语音唤醒(2026-10-15 实测,分支 local-voice-stack)

现象:ASR/TTS 正常、字典正常,唤醒词只响一次提示音,之后再也不响。

### V0 根因(已复现,最高优先)

- [x] **V0.1 只喊唤醒词、不说话 → 唤醒会话永不回闸,此后不再探测**(2026-10-15 修复)
  现状:`_awake` 只在 `SubmitAsync` 的**成功路径**清;三条提前 return——块 < `MinBlockSeconds` / 音频被环形冲掉 / `SpeechFraction < MinVoicedFraction`——全部跳过它。而 `AsrStream=false` 时 G2 的 `SegmentEnded` 恒为 false(`PauseBlockingDetector.cs:175-187`),所以 `TickAsync` 里那条兜底清闸永远走不到。加上唤醒命中时 `Detector.OnGateReleased(Ring.Seconds)` 把游标推到**命中那一刻**,唤醒词本身的音频落在游标之前被丢——只喊词不说话时块里只剩静音 → 撞上提前 return → `_awake` 卡 true → `PollWakeAsync` 因 `!_awake` 永不执行 → 提示音再也不响,而闸门一直开着(此后所有话都不经唤醒词直接进 ASR)。
  复现(app 输入指向 VB-Cable,用音频喂,绕开麦克风):喂「小克小克」→ 无转写;喂「今天天气怎么样」(**不含唤醒词**)→ 被转写,证明闸门仍开;再喂「小克小克」→ 仍无转写。
  改法(两处,缺一不可):
  1. `PauseBlockingDetector.OnWakeHit(now)`——与 `OnGateReleased` 同体,额外把 `_dirty` 立起来(唤醒词本身就是「已经听到人说话」)。不立则喊完词不说话时 G2 永不成立,会话照样卡死。
  2. `VoiceInputHalf.TickAsync` 在 `SubmitAsync` 之后收尾:`_awake && Trigger == UtteranceGap && Pause` 即关闸,**与提交结果解耦**;原先 `SubmitAsync` 成功路径里那段删除。三条提前 return 现在都走同一段收尾。
  命中时游标仍定在 `Ring.Seconds`(即丢弃唤醒词自身音频)——刻意保留:唤醒词不该被转写后送进 LLM,实测唤醒词后紧跟的命令能完整进块。若以后出现「命令首字被掐」,再改成定在探针增量起点。
  验收:`WakeGateTests.A_wake_hit_without_following_speech_closes_the_session`(新用例)——只喊词不说话(2.5 s 静音)后 `Awake` 必回 false、无 utterance、探针重新跑起来、再喊一次再次进会话。已验:把 `OnWakeHit` 换回 `OnGateReleased` 该用例必红(实测 `Expected: False / Actual: True`);`Partner.Voice.Tests` 85/85 绿。

- [x] **V0.2 单声「小克」即命中双声唤醒词 → 探针把每句话喂了两遍**(2026-10-15 修复)
  复核:原判断(引擎/词表把单声认成双声)**不成立**。干净整段喂入同一引擎(venv `sherpa_onnx` 1.13.8,`epoch-12-avg-2` int8):单声「小克」→ 无命中;双声「小克小克」→ 命中 `小克小克`;不含唤醒词的句子 → 无命中。词表写法与 `keywords_threshold=0.25` / `keywords_score=1.5` 都对:阈值/加分扫描(0.25/0.5/1.0/2.0/4.0 × 1.5/3.0/6.0)里,0.25+1.5 是唯一同时「双声命中、单声不中」的一档,0.5 起连双声都不中。探针音频内容用 SenseVoice 回读确认过(`wake_single.wav` = 「小克。」),不是把双声当单声喂。
  真因:**探针时序**。帧是「[游标-1s, 游标] + 增量」,于是上一帧的增量正好落在下一帧的前置里——同一条持久流把每句话消费两遍,两遍「小克」凑出双声词。实测(复刻宿主 100ms 节奏,对真引擎 `/wake/infer`):旧时序单声 5 次命中、双声 10 次;只首帧带前置则单声 0 次、双声 1 次。
  改法:`VoiceInputHalf.PollWakeAsync` 只在引擎那条流是新的时送前置(`_wakeNeedsLead`:首帧、命中换流、探针失败即引擎可能重启、游标重同步),之后只送增量。引擎侧与词表不动。
  验收:`WakeGateTests.Wake_probe_primes_the_stream_once_then_sends_bare_increments`(首帧带前置、其后不得 ≥1.1s——把 `_wakeNeedsLead` 改回恒真该用例必红)+ `Wake_probe_re_primes_the_stream_after_a_hit`(命中后首帧必须重新带前置:>2s 只可能来自前置,增量上限就是 2s);真引擎往返见上。

### V1 三处真缺陷(诊断能力,与 V0 无关也该修)

- [x] **V1.1 `VoiceInputHalf` 的日志全是死代码**(2026-10-15)
  改法:`VoiceHost.StartInput` 传 `log: VoiceLog`(落 stderr,与 `LocalEngineHost` 同一路);文案统一 `voice: ` 前缀,`input started` 顺带报 `wake=on/off`。另修一个会随之暴露的问题:`wake poll failed` 原来每 tick 一条,引擎挂掉时把 stderr 淹掉——现在只在「第一次」和「换了原因」时打,恢复时补一条 `wake probe recovered`。
  验收:接线已通;实跑 stderr 应出现 `voice: input started (device=… threshold=… wake=on)`、命中行(带 keyword 与 confidence)、探针失败原因、阈值误配告警。

- [x] **V1.2 `/api/voice` 不报输入电平**(2026-10-15)
  改法:`VoiceInputHalf.RecentRms`(最近 0.5s,每 100ms 刷一次,门内门外都刷);新增 `VoiceInputState` = `VoiceHalfState` + `rms`/`ambientRms`/`thresholdMisconfigured`/`awake`,`/api/voice` 的 input 用它(output 仍是 `VoiceHalfState`,不给它安无意义的字段)。设置页:电平条(阈值刻度画在同一条上——「够不够得着阈值」正是要判断的事)、底噪误配红字、唤醒门开着时「已唤醒/等唤醒词」;电平只在本视图挂载期间按 500ms 轮询。
  顺带修一个会被这次暴露放大成假告警的缺陷:底噪原来在 `now>=0.5s` 那一帧**无条件**取,用户一开机就说话就量到说话的能量、报出「阈值误配」;改成等真正安静的一帧,5s 等不到才按这段时间最安静的一帧如实上报(并在日志里说明「no quiet window in the first 5 s」)。
  验收:Core 单测 `InputLevelTests`(电平跟随音频;说话期间的能量不算底噪;阈值 0 与永不安静的屋子都在 5s 后上报);smoke 三条(电平升、电平落、开机说话不被当底噪);前端 `views.test.ts` 两条(电平条含阈值刻度、误配红字)。

- [x] **V1.3 ASR 测试没识别出内容仍报 Ok**(2026-10-15)
  改法:判定抽成纯函数 `VoiceTestEndpoints.JudgeAsr`——整段 RMS 低于 `input.silenceThreshold` 即 `Ok=false`「没听到声音(RMS x 低于阈值 y):设备无信号,或测试时没说话」,引擎从静音里「转出」文本也不翻绿(音频是证据,转写是引擎的意见);有声音但没转出文本仍算通过,但把 RMS 写进说明,两种「绿」可区分。KWS 测试补真实往返:录 3s 喂 `/wake/infer`——命中报「命中「小克小克」」,有声音没命中如实说明(测试时是否说了唤醒词),没声音/录不到 = 红,采样率非 16k 时写「跳过」。
  验收:`Partner.Api.Tests/VoiceTestEndpointTests` 六条(静音红、1 LSB 底噪红、幻觉文本不翻绿、有声音有文本绿、有声音无文本绿且带 RMS、设备打不开的静音探测红);KWS 的真实往返需真机(拔麦即红)。

**smoke 已跑(2026-10-15,应用未开)**:首跑 SMOKE OK,`voice.input_level` 三条全 PASS;新能力已锁进 `capabilities/manifest.baseline.json`(69 能力 / 25 行纯插入 / `checksFailed: 0`)。

注意:后两跑各挂一条**桌面**检查(`desktop: px/py on the desktop picture resolves to the point it was taken from`、`desktop: an approved action looks again instead of trusting a dead lease`),各自把所属能力(`desktop.desktop_scope` / `agent.queue_consumer`)从 product-supported 拖成 covered——同一台机器连跑三遍结果不同,是既有环境敏感抖动,与本批语音改动无关。D1 批复核:把全部改动 `git stash` 后于干净 HEAD 重跑,同一条 px/py 检查仍红、报错逐字相同(msedge 窗口在 observe 与 act 之间变动),证明确非 D1 改动所致;当前桌面布局下这条已从「偶发」变「持续」,跑 `--verify-baseline` 必红。因此 baseline **不**从抖动跑里写(`--update-baseline` 会把 `passed:false` 与降级一并写进去,还会掩盖以后真降级),改为手工只插 `voice.input_level` 一段。副作用:本机跑 `scripts/ci.sh` 的 `--verify-baseline` 门禁同样会随机红在桌面检查上。

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

- [x] **M1.1 CJK 真分词(替换 bigram LIKE OR 全表扫描)**(2026-10-15)
  改法:新 `CjkText`(bigram 切分、`Index` 出「空格分隔的 CJK bigram 串」、`MatchExpression` 出 FTS5 表达式)统一持有那套规则;`memories` 加真列 `body_cjk` + 第二张 FTS5 外部内容表 `memory_fts_cjk`(与 `memory_fts` 同三触发器写法),写/改/软删三处同步维护;CJK 查询走 `MATCH` + bm25,与 Latin 同一 `CandidateLimit`,旧 LIKE 分支与 `Escape`/`BuildLikePattern` 删除。既有库靠 **v1→v2 迁移步骤**回填:`SchemaMigration` 新增 `Data` 委托(`Action<SqliteConnection>`,在 `Statements` 之后跑),因为 bigram 算不出 SQL——逐行算 `body_cjk`,再用索引自己的 `rebuild` 命令建索引。
  两个实测坑(都写进代码注释):① 对**未入索引**的行 UPDATE 会触发 FTS5 的 `'delete'` 命令撞空 → SQLite 直接报 `database disk image is malformed`;② `CREATE TRIGGER` 在 **prepare 期**就解析列名,老库上先建引用 `body_cjk` 的触发器会**开不了库**(`no such column: new.body_cjk`,Microsoft.Data.Sqlite 的 SQLite 版本如此;python 的 sqlite3 3.50 反而推迟解析,别拿它当依据)。因此 DDL 拆成「无条件部分」+「触发器单独一批」,触发器由 store 在**迁移之后**创建;回填时它们还不存在,索引由 `rebuild` 建。
  验收:`CjkRecallTests` 七条(换词问句命中、无共同 bigram 不命中、中英混排记录能被中文半边找到、改正文后新旧措辞一进一出、软删后召回为空、`RebuildIndexAsync` 后仍命中、无中文的正文不进索引);`MemorySchemaMigrationTests.An_old_store_backfills_the_bigrams_its_rows_were_never_indexed_with`(老库中文行迁移后可被换词问句召回 + 迁移后新写入照常进索引);smoke 新能力 `memory.scale_p99_cjk`(10 万中文库 P99 <500ms,本机 median 84.9ms / p99 125.3ms,seed 9.5s)。
  实测对照(临时脚本,10 万行、查询词只在 5% 的行里出现):旧 LIKE-OR 扫描 median **65.0ms** / p99 67.7ms → 新 bigram 索引 median **15.2ms** / p99 17.4ms。注意第一次用「每行都含查询词」的语料量,两条路径都是 200 行、耗时也接近(41ms vs 82ms,索引反而更慢——FTS 在排序一个几乎全中的候选集),那测的是排序不是检索,已换成稀有词语料。

- [x] **M1.2 `FindBySourceAsync` 建索引**(2026-10-15)
  改法:DDL 加 `ix_memories_source ON memories(source_event_seq)`(既有库靠 `CREATE INDEX IF NOT EXISTS` 自动补上,无需迁移步骤);接口加 `FindBySourceRangeAsync(from,to)`——一条 `WHERE source_event_seq BETWEEN $from AND $to AND valid_to IS NULL`,`MemoryDeletion.CascadeAsync` 改用它,原来「逐 seq 走一遍」的循环和「列没索引所以走一遍更便宜」的理由一并删掉。`FindBySourceAsync`/`FindBySourceRangeAsync` 各自给 `MemoryStore.SourceQueries` 记一次数(`internal`,给测试用:`internal` 可见性靠新增的 `InternalsVisibleTo("Partner.Core.Tests")`)。
  验收:`MemoryHookTests.Deleting_a_long_span_reads_it_in_one_query`——3 条记录落在 seq 100/500/900,级联 1..1000 后 `removed == 3` 且 `SourceQueries == 1`(逐 seq 版必红:1000)。既有两条级联用例不回归。

### M2 数据层稳健性

- [x] **M2.1 schema 版本号 + 显式迁移**(2026-10-15)
  改法:`PRAGMA user_version` 打版本,`MemorySchema.Version`(当前 1)+ `MemorySchema.Migrations` 步骤表驱动(`SchemaMigration(To, Description, Statements, TolerateFailures)`,升序执行、每步之后立刻打版本戳,中途崩只退到上一个完成的步)。现有「试跑吞异常」归为 v0→v1 的兼容步骤(`TolerateFailures: true`,只有明说这一步的失败才是预期结果)。新库不走步骤——DDL 就是当前 schema,直接盖当前版本戳(`memories` 表在 DDL 之前是否存在决定走哪条路)。库版本 > 本 build 已知版本即拒开(读了只会瞎猜,写了可能丢新 build 加的列);拒开时连自己那条连接一起 dispose,否则文件被这个进程锁着。
  验收:`MemorySchemaMigrationTests` 六条——旧库(手工造 v0 形状 + 真 DDL 补齐 FTS/触发器/索引,再插一行)打开后版本盖章、列存在、原记录还在、降级扫描(唯一读 `last_recalled_at` 的查询)真的能跑、写与召回正常;已有该列的库照样打开;新库直接是当前版本;重复打开不重复迁移;`user_version = Version+1` 的库被拒(`newer`);步骤表自检(号不超过当前版本、有描述、有语句)。
  坑:v0 夹具必须**先建表、再跑真 DDL、最后插行**——行插在触发器存在之前,更新它时 FTS5 的 `'delete'` 命令撞上不存在的索引项,SQLite 直接报 `database disk image is malformed`(实测)。

- [x] **M2.2 `ParseScope` 未知值显式记录**(2026-10-15)
  改法:兜底行为不变(未知值仍当 `Recall`),但不再静默——`ParseScope` 变成实例方法,按值计数存进 `UnknownScopes`(`internal`),第一次遇到某个值打一条 stderr(同类值只打一次,不刷屏)。理由:整库 scope 全是本 build 不认识的值,表现和「全部被悄悄放掉」一模一样,没有别的信号会说出来。
  验收:`MemoryStoreTests.An_unknown_stored_scope_reads_as_recall_rather_than_throwing` 加断言——`Global` 计数 1、且只有它一个键(可读的 scope 不留记录)。

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

- [x] **D1.1 遥测 windowHours 与 sink 保留期联动**(2026-10-15)
  现状:聚合端点硬编码 1h(`DesktopEndpoints.cs:72`),sink 保留 1h——改其一即静默不一致。
  改法:`InMemoryTelemetrySink` 加 `DefaultRetention` 静态字段 + 实例 `Retention` 属性(构造可传);`ApiContext` 带 `Clock`;端点从 `ctx.Telemetry.Retention` 派生窗口,`hoursRaw` 只可收窄不可放宽(`Math.Clamp(0, retention)`),响应回 `windowHours` + `retentionHours`。
  验收:`The_telemetry_window_is_the_retention_it_is_read_from`(端点窗 == sink 保留期)+ `A_caller_may_narrow_the_telemetry_window_but_not_widen_it`(0.25 收窄生效、999 被 clamp 到保留期、负数归 0)。

- [x] **D1.2 `desktop_exec` 后台子进程管道关闭**(2026-10-15)
  现状:30s 超时后管道可能不关(`DesktopSystemTools.cs:124-126` 自认),子进程与句柄滞留。
  改法:超时路径 kill 进程树后新增 `ClosePipes(process)` + `WaitForDrainsAsync(outTask, errTask)`(1s `DrainCloseGrace` 兜底)。仅杀进程树不够:`start /b` 这类脱离子进程仍持有继承的写句柄,匿名管道读在读到数据前不响应取消,两条 drain 就停在无人持有的 reader 上。
  **更正(实测推翻原判)**:原以为「修前 5 句柄、修后 1」,是噪声。10 次超时命令 × 4 轮句柄计数,修前/修后均平(`grew` 在 5/1/1/-6 与 5/0/1/-5 间抖动)——`KillQuietly` 用 `Kill(entireProcessTree: true)`,`start /b` 子进程已被回收,管道随 EOF 关闭,本就不泄漏。此改动是无泄漏场景的防御性加固(覆盖「杀树前已脱离子进程/启动器比杀更长寿」),不是修一个已复现的泄漏。原 `grew <= 2` 测试钉的是噪声,已删。
  验收:`Exec_a_timed_out_command_reports_that_it_closed_its_pipes`——超时结果如实声明「进程树与管道均已关闭」,且不挂起。**不声称句柄不泄漏**(无可复现泄漏)。

- [x] **D1.3 并发 observe 共享 walks/truncated**(2026-10-15)
  现状:并发 observe 互相覆盖 walks/truncated(`DesktopAutomation.cs:185`,注释假设工具循环单线程);步预算 4 并行已在打破该假设。
  改法:`_walks`/`_truncated` 字典换成不可变 `WalkSnapshot` 记录(`volatile _walkState`),per-call 建字典、末尾整体发布;`_lastObservation` 加 `volatile`;构造加 `walk` 接缝以驱动交错。
  验收:单测 `ObservationConcurrencyTests`。**关键**:第一版测试旧码也过(交错没落在真正的窗口上),重写为「定向 observe 停在自身 walk 中 → 全量 observe 清空共享字典并跑完 → 定向 observe 读出别人的截断原因」。回退到共享字典实测红(`Expected: False / Actual: True`,拿到的正是并发 walk 的 `Visited`),新码绿——测试确实锁住该修复。

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

- [x] **C.3 README 坑 9 已过时清理**(2026-10-15)
  改法:坑 9 从「尚未纳入版本控制(没有 `.git`)」改成现状(默认分支 `main`、改动先开分支、改能力要连基线一起提交);顺检坑列表其余条目,未见其它过时项(坑 10/11/12 记的都是已修的事与仍成立的约束)。
  验收:`node scripts/check-docs.mjs` 过(ci.sh 首步,设计文档引用仍解析)。

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
3. **M1.1 + M1.2**(检索质量,依赖 M2.1 的迁移框架先行)**✅ 2026-10-15(M2.1 → M1.2 → M1.1 全部完成)**
4. **D1.1–D1.3 + C.3**(小修,可穿插)**全部 ✅ 2026-10-15(C.3 → D1.1 → D1.2 → D1.3)**
5. **M2.x → M3.x → C.4**(数据层→覆盖面→低优先;M3.1 已随 M0 批完成;**M2.1/M2.2 ✅ 2026-10-15**)
6. **C.1/C.2**(人工验证,不占开发周期,有真实端点时顺手)

收尾清单(每期完成后勾):
- [x] smoke 新检查写进 `src/Partner.Smoke/Program.cs` + `Capabilities` 表(M0 批:4 条新检查、injection_budget 加 1)
- [x] `dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json`(2026-10-13 已刷新:163 检查 / 68 能力 / 66 product-supported;含上轮待提交的 desktop.telemetry_aggregates)
- [x] README 相应小节补行(含未做/未验证表)
- [x] 跑 smoke 前停应用(本轮连跑两遍均 SMOKE OK;px/py 检查为环境敏感,无应用干扰时通过)
