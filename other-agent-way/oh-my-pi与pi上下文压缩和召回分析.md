# oh-my-pi 与 pi：上下文压缩和召回机制分析

## 1. 范围与结论

本报告以本机源码为准，不代表项目之后的实现：

- **oh-my-pi（下称 OMP）**：`D:/code/Clone/oh-my-pi`，commit `acdfffa248d1cdb5e0a05173e44e441103a37daf`，仓库 <https://github.com/can1357/oh-my-pi>。
- **pi**：`D:/code/Clone/pi`，commit `edfe6b2ef32283d31efc9e26db86fe48be19d3b2`，仓库 <https://github.com/earendil-works/pi>。
- 重点是 coding agent 的会话上下文；OMP 的长期记忆以仓库内 Mnemopi 为主要案例。Hindsight 是外部服务，本报告只分析 OMP 的接入语义，不推测服务内部算法。
- 文中“默认”指上述快照中的默认配置，不保证适用于其他版本、模型或用户配置。

**核心结论：**

1. 两者都将会话持久化为可分支的事件历史；压缩主要改变**下次发送给模型的投影**，并不等于删除原始历史。两者都通过“旧摘要 + 新近原文”续写，并在树导航时可生成分支摘要。
2. pi 的默认方案偏精简：以 token 预算决定切点，LLM 生成结构化滚动摘要，保留近期完整消息。它不是一个内置的跨会话向量记忆系统；原始记录可供恢复、导出或扩展检索，但不会因为压缩而自动进行语义检索。
3. OMP 在相同基本范式上增加了多种压缩策略、工具输出裁剪、故障恢复、历史访问以及可选长期记忆。**压缩**解决窗口容量，**召回**解决按需找回信息，二者不应混为一谈。
4. OMP 的长期记忆默认 `memory.backend: off`。只有显式启用特定后端才有对应的跨会话行为；`local`、`mnemopi`、`hindsight`、`sharpshooter` 的能力和一致性模型不同。
5. 不论方案多复杂，摘要、截断、图片归档和检索都可能丢失细节；原始历史可用、结果带来源且能按需回读，才是可靠恢复的关键。

## 2. 先区分四种“找回上下文”

| 机制 | 触发 | 信息来源 | 是否查询相关性 | 主要用途 |
|---|---|---|---|---|
| 压缩摘要重放 | 达到阈值、溢出恢复或手动压缩后的后续请求 | 当前分支此前的摘要 | 否，默认每次放入活动上下文 | 延续当前任务状态 |
| 近期原文保留 | 压缩时按预算选择 | 当前分支最近消息 | 否，以时间顺序和合法切点选择 | 保留最近精确上下文 |
| 分支摘要 | `/tree` 切换时选择总结 | 离开分支相对公共祖先的增量 | 否 | 把另一条尝试的结论带入目标分支 |
| 历史检索/长期记忆 | 显式工具调用，或后端配置的自动召回 | 历史记录或跨会话记忆库 | 历史搜索可按文本；Mnemopi 可混合关键词/向量等 | 按需恢复细节或复用长期知识 |

因此，压缩摘要再次发送给模型是一种**重放**，不是 RAG 意义上的语义召回；会话文件中仍有原始数据，也不意味着模型能自动看到或检索到它。

## 3. pi 的上下文压缩

### 3.1 触发和预算

主要实现在 `packages/coding-agent/src/core/compaction/compaction.ts`，说明见 `packages/coding-agent/docs/compaction.md`。

自动阈值的基本形式为：

```text
contextTokens > contextWindow - reserveTokens
```

- `reserveTokens` 默认 16,384；它主要控制何时触发，不等同于摘要输出上限。
- `keepRecentTokens` 默认 20,000；从最近历史向前累计估算 token，决定尽量保留的近期部分。
- 工具执行结果写入后、下一次 assistant 响应前会检查；新用户提示前也会检查。provider 上下文溢出或某些过早的 `length` 结束可触发一次压缩重试；也可 `/compact [instructions]`。
- 总结请求的输出限制由模型输出限制及剩余上下文共同约束。阈值判断是预算策略，不是 tokenizer 精确保证。

### 3.2 切点、增量摘要、上下文重建

```text
当前活动分支投影：  [旧摘要] + [待摘要旧消息] + [近期原始消息]
                                      ↓ LLM
新增 compaction 记录：{ summary, firstKeptEntryId, tokensBefore, ... }
下次模型输入：      [system] + [新摘要] + [firstKeptEntryId 起的近期消息]
```

1. 从近期消息向前估算 token，选择保留边界；优先在用户消息等合法位置切分，**不能在 tool result 上切开**，避免把工具结果与调用拆散。
2. 从上次保留边界而不是简单从上次 compaction 记录之后开始，将此次不再保留的旧消息送去总结；旧摘要也作为迭代输入，避免上一次保留的历史被漏掉。
3. 如果单个用户消息跨度过大，切点可能落在跨度内部：分别生成历史摘要和该跨度前缀摘要，再合并，保留后半段原文。
4. 追加 `CompactionEntry`，而非把摘要当作普通 assistant 消息覆盖原记录。重建模型上下文时从活动路径选择压缩记录，插入摘要并重新包含边界后的原文。
5. Pi 的 `context_edit` 可改变实际发送投影；溢出恢复会对失败尝试追加 omission，压缩失败时不会凭空提交成功摘要或继续内部重试。原始记录仍可供历史查看等用途。

摘要结构强调 `Goal`、`Constraints & Preferences`、`Progress`、`Key Decisions`、`Next Steps`、`Critical Context`，并累计记录读取/修改文件。序列化时忽略 thinking，工具结果截断至 2,000 字符，以降低总结请求体积；因此不能假设摘要包含每一行工具输出。

### 3.3 分支摘要不是常规压缩

`packages/coding-agent/src/core/compaction/branch-summarization.ts`：计算旧 leaf 与目标节点的最近公共祖先，收集旧 leaf 到该祖先之间的离开分支内容；按照预算优先取最近消息，用 LLM 总结后在目标分支追加 `BranchSummaryEntry`。被离开的分支本身不因导航而删除，已有 compaction 摘要也可作为分支摘要输入。它的目标是迁移另一条尝试的经验，而不是为当前分支单纯腾空间。

扩展可通过 `session_before_compact` 和 `session_before_tree` 取消或提供自定义结果，见 `packages/coding-agent/docs/compaction.md` 与 `src/core/extensions/types.ts`。这提供自定义模型、检索和压缩策略的接入点，但不等于内核默认已提供这些能力。

## 4. OMP 的压缩与上下文维护

主要参考 `docs/compaction.md`、`packages/agent/src/compaction/`、`packages/coding-agent/src/session/session-maintenance.ts`、`session-context.ts` 和 `compaction-methods.ts`。

### 4.1 共同骨架与触发

OMP 仍使用 `CompactionEntry` 的 `summary`、`firstKeptEntryId` 等信息重建当前分支；分支摘要也基于最近公共祖先。多次本地摘要时，之前保留的原文会重新进入“待摘要、拆分轮前缀、近期保留”三者之一，并将上一摘要单独传入。

除了手动 `/compact`，还支持阈值、工具循环中的安全边界、空闲、上下文溢出和不完整输出等维护路径；部分路径先尝试切换到更大上下文模型，再压缩。长度截断若实际上是输出上限而非输入过大，不一定通过压缩解决。自动维护有进度检查：无法产生足够空间时尝试后续方法或停止，而不是无限重试。

默认 `keepRecentTokens` 为 20,000；阈值还受固定 token、百分比、模型覆盖及 reserve 策略影响，不能统一写成一个固定百分比。上下文统计会考虑实际 provider usage 与本地估算，并剔除部分不会重放的 provider 编排 token。切点仍不能落在工具结果上。

### 4.2 默认方法顺序与差异

`packages/coding-agent/src/session/compaction-methods.ts` 的默认顺序为：

```text
remote → snapcompact → handoff → shake → soft
```

方法是否适用依赖模型、配置、触发原因和前一方法的结果；不是每次必然顺序执行全部方法。

| 方法 | 核心做法 | 主要取舍 |
|---|---|---|
| `remote` | 使用符合条件的 provider 原生压缩或配置的远程端点 | 可使用供应商原生上下文；某些 OpenAI payload 不透明，跨 provider/model 重放有限制 |
| `snapcompact` | 将裁剪和序列化后的历史绘制为 PNG 帧，连同时间轴两端文本重放 | 无总结模型调用，但要求视觉输入，受图片 token、清晰度、字节和帧数限制；不是无损压缩 |
| `handoff` | 基于当前上下文生成面向接手者的任务交接文档，作为压缩摘要 | 对目标、状态和下一步友好；生成耗时，仍是有损 LLM 摘要 |
| `shake` | 本地替换可消减的大块内容，保留可恢复的 `local://N.log` 引用 | 避免摘要调用并可按引用回读；不是所有内容都适合消减 |
| `soft` | 使用模型生成常规文本摘要 | 跨模型可读，语义抽象，但可能遗漏或幻觉 |

`snapcompact` 先序列化并截断工具输出等，然后将中间历史渲染成图片；中文等宽字符可使用适配字体。其帧数、窗口、图片和字节预算会限制保留量，过载时可能舍弃较旧帧。不能把项目中的图像归档评测描述推广为所有模型、语言和任务的召回保证。

原生压缩存在 replay 兼容性问题：OMP 会依据目标模型能否读取 payload 选择可读的压缩边界；切换到不兼容模型时可能重新暴露更多原始历史，并在新模型请求前再次压缩。OpenAI 类 payload 不应当作可由其他模型直接阅读的文本摘要；Anthropic 路径在适用条件下同时保存可读摘要和签名块。

### 4.3 压缩之前先减少噪声

`packages/agent/src/compaction/pruning.ts` 中的工具输出裁剪与摘要是不同层：默认保护最近约 40,000 个工具输出 token，至少节省约 20,000 token 才执行常规裁剪；过小结果不裁剪，受保护的 skill 和计划文件读取不应被清掉。被裁剪的结果可变成 `[Output truncated - N tokens]`。

无用结果、被后续读取替代的结果还有专门策略，并考虑 prompt cache；压缩文档说明，某些 pruning 会**重写会话存储**。因此不能笼统承诺所有原始工具输出永久可恢复：区分普通压缩的投影变化、可回读的 shake spill 和可能具有破坏性的 pruning/图片丢弃。

### 4.4 可选的 notes + history 模式

`compaction.experimentalContextManagement: true` 启用实验模式后，自动维护可改为由模型维护 `context_notes`，用 `new_context` 在安全边界滚动窗口；笔记限制 16,384 UTF-8 字节，最新可见版本注入上下文。`history://current/full` 支持通过 `read` / `grep` 搜索当前分支原始历史并使用稳定 entry ID。该模式依赖必要工具可用，且不保证恢复此前已被破坏性删除的内容；图片在文本历史中是标记。笔记是否及时、完整由模型负责。

思路是把“每次都重新总结”替换为“显式维护工作笔记 + 可搜索的外部历史”，以可控恢复路径对冲滚动摘要信息衰减。

## 5. OMP 的长期记忆与召回

权威入口为 `docs/memory.md`、`docs/mnemosyne-memory-backend.md` 和 `packages/coding-agent/src/memory-backend/`。所有记忆注入均应作为**背景材料而不是指令**；当前用户请求及实际工具结果优先。

### 5.1 多后端并不等价

| 后端 | 数据组织与写入 | 读取/召回特点 |
|---|---|---|
| `off` | 默认关闭 | 没有该长期记忆后端 |
| `local` | 后台按 session 提取，再跨 session 整合成 `MEMORY.md`、`memory_summary.md` 和 skills；`learned.md` 单独维护 | 下次 session 注入紧凑摘要和 lessons；可通过 `memory://root` 等读文件；不提供结构化 `recall`/`retain`/`reflect` |
| `mnemopi` | 本地 SQLite，按 bank 作用域存储，支持自动保留与显式工具写入 | 首轮检索注入，支持 `recall`、`retain`、`reflect`、`memory_edit`、按 ID 全文读取 |
| `hindsight` | 远程 Hindsight 服务，项目/全局 bank 和标签作用域 | 首轮自动召回、定期保留；显式写入有本地 durable outbox；服务端检索算法不由此仓库证明 |
| `sharpshooter` | 提取用户决策并合并到 architecture/product/style 文件 | 注入项目决策文件，不等同向量检索 |

`local` 的两阶段是“每个旧会话提取持久信号 → 汇总所有提取结果”，不是把所有历史原文永远塞入 prompt。默认扫描、年龄、输入和注入均有限额；例如 summary 与 lessons 共享约 5,000 token 注入预算。后台管线有 lease/heartbeat、防重复及秘密脱敏，但脱敏是降低风险而不是安全保证。

### 5.2 Mnemopi 生命周期和作用域

默认启用 Mnemopi 后：首个模型轮次根据任务召回、注入 `<memories>`；完成的对话按 `retainEveryNTurns` 自动保留，默认每 4 个用户轮次最多一次。默认首轮最多 8 条、查询包含最多 3 个此前用户边界轮次、查询最多 4,000 字符、注入约 5,000 token。压缩需要时，后端也可提供额外上下文；这不是每种压缩都一定调用记忆检索。

默认 `per-project` 按当前工作目录派生独立 bank；`global` 使用共享 bank；`per-project-tagged` 写项目 bank，读取项目 + 全局 bank 并去重。首轮召回结果作为 `memory_recall` 持久化，resume/fork 等通常重用，稳定 prompt cache；重置、变更记忆库或编辑分支首个 prompt 时可能重新召回。记忆被删除后，会话文件里以前注入的内容仍可能存在；因此删除记忆库不等于删除所有历史副本。

`recall` 返回预览，默认最多 500 字符并标记截断；`read memory://<id>` 获取完整记录及元数据，尤其在 `memory_edit update` 替换全文前必须读取，避免把预览误写成全文。自动 retain、显式 retain、consolidation 是不同阶段；立即 `/memory enqueue` 并不绕过 sleep consolidation 的年龄门槛。

### 5.3 Mnemopi 检索和排序算法

核心源码：`packages/mnemopi/src/core/beam/recall.ts`、`polyphonic-recall.ts`、`query-cache.ts`、`beam/index.ts`；以下是该快照代码的概括，不是对外部 Hindsight 的描述。

1. **构造查询与候选**：从 query 提取关键词/同义词；从 working 与 episodic 两类记录收集全文检索和向量候选；可使用 embedding。向量不可用或 `noEmbeddings: true` 时可退化为关键词路径。默认英文 embedding 对中文未必最佳，可配置 multilingual 模型；更换 embedding 模型涉及重建向量。
2. **候选过滤**：按词法相关性和 dense 相似度过滤明显不相关项；代码中词法不达最低要求且 dense 小于 0.65 的候选被过滤。权限、scope、有效期等过滤不能用相关度排序代替。
3. **基础打分**：episodic 路径使用向量、FTS、重要性归一化加权，并与词法得分下限比较；`beam/index.ts` 默认权重为 `0.5 / 0.3 / 0.2`。working 路径采用不同的关键词/重要性公式，并在有向量时混合 dense 分数，所以不能把两者都简化为同一个公式。
4. **时间与可信度修正**：基础分乘以近期衰减因子；带时间意图时再按记录时间或事件时间增强。再乘 `veracity` 权重，episodic 还乘降级 tier 权重。默认 stated 1.0、unknown 0.8、inferred 0.7、imported 0.6、tool 0.5；tier 1/2/3 分别 1.0/0.85/0.7。它们是排序权重，不是事实真伪概率。
5. **去重与多样性**：按分数排序，去除跨层重复；长查询可奖励尚未覆盖的关键词；可选 MMR 平衡相关度和重复度，默认 lambda 参数为 0.7（当该重排启用时）。最终取 top-K 并记录召回统计。
6. **可选 polyphonic**：默认关闭。启用后将标准 hybrid 排名和 vector、graph、fact、temporal 四路排名做 Reciprocal Rank Fusion；每路第 `r` 名贡献 `1 / (60 + r)`，再融合。图/事实/时间通道可以找出未直接命中关键词的关联；不同 voice 的可信度权重使用方式并不完全相同。
7. **可选 enhanced recall cache**：默认关闭。相同选项下复用相同或足够相似的查询，缓存最多约 5 分钟，bank 写入会失效；语义相似查询复用是一项性能/准确性权衡。

概念上的 episodic 排名可写为：

```text
base = max(w_vec × dense + w_fts × fts + w_imp × importance,
           0.8 × lexical)
score = base × (0.7 + 0.3 × recency)
       × optional_temporal_boost × veracity_weight × tier_weight
       × current_content_adjustment
```

这是代码路径的简化表达：候选过滤、归一化、时间选项及其他处理均会影响实际结果；不能用它预测所有 working-memory 或 polyphonic 分数。

### 5.4 Hindsight 接入语义

OMP 默认每 3 个用户轮次定期 retain，首轮自动 recall；具体可配置。显式 `retain`/`learn` 通过磁盘 outbox 重试，服务端 2xx 仅表示接受而非已完成抽取；自动 transcript retention 不使用该 outbox。`/memory clear` 清本地状态和召回缓存，**不会删除远程 bank**。服务不可用时主会话尽量继续。项目隔离和 global 可见性由 bank/tag 策略决定，不能将其与 Mnemopi 的本地 bank 实现混为一谈。

## 6. 对比与设计取舍

| 维度 | pi | OMP |
|---|---|---|
| 主压缩骨架 | LLM 滚动摘要 + 最近原文 | 相同骨架，加多种策略与维护编排 |
| 阈值与恢复 | 窗口减 reserve、轮次间检测、溢出恢复 | 多种阈值覆盖、mid-turn/idle、模型提升、策略回退和进度保护 |
| 信息精度 | 最近原文精确，旧摘要有损；摘要输入工具结果截断 | 多层处理更灵活；剪枝、摘要、图片归档各有损失边界 |
| 分支迁移 | 公共祖先差集 → 分支摘要 | 相同基本思路 |
| 历史回读 | 原始 session 仍可查看/扩展处理；默认不等于语义记忆 | 除会话历史外，有实验性的当前分支全文 `history://` 及多种 memory 后端 |
| 跨会话检索 | 本报告检查的内核未见默认长期语义召回；可扩展 | 默认关闭；Mnemopi 等启用后提供检索与持久记忆 |
| 工程复杂度 | 机制少、行为相对易解释 | 能力广，但策略兼容、失效、数据生命周期与成本更复杂 |

### 风险和边界

- **摘要漂移**：多次摘要把事实压成更少 token，旧错误可能被反复继承；保留原始来源、关键路径和精确约束比一味压短更重要。
- **检索误召回**：相似不等于正确，旧版本决策、项目混淆和低可信来源可能污染回答；应展示来源、时间、scope 和可信度，并允许失效/删除。
- **缓存与隐私**：缓存首轮召回有利于稳定与节省费用，但数据变更后不等于历史提示立即被撤销；会话文件、导出、远程 bank 与 spill 文件需分别管理。
- **工具调用一致性**：保留/压缩边界必须维护 tool call 与 result 配对，不能仅按字符或 token 任意切割。
- **成本**：增加摘要请求、embedding、rerank、图片和存储会带来不同成本；prompt cache 命中率、模型视觉计费与延迟也应纳入，而不只是压缩率。

## 7. 对 Partner 的可落地建议

以下是基于源码的设计建议，不表示 Partner 当前已有对应功能。

1. **先做可重建投影**：会话日志使用 append-only entry、稳定 ID、parent ID；让模型输入是日志的投影。先实现 `summary + firstKeptEntryId + recent`，日志导出与 UI 展示独立于投影。明确列出所有破坏性操作。
2. **摘要存成结构化检查点**：至少保留目标、约束、已完成/未完成、决策及原因、错误、下一步、文件路径/版本和来源 entry ID。支持增量摘要、拆分长轮次和工具调用原子边界。
3. **检索按两层演进**：先提供当前分支历史的精确全文搜索与按 ID 回读；再引入按项目隔离的长期记忆。不要将旧摘要直接等同于已验证事实。
4. **长期记忆用混合召回**：关键词 + 向量 + 时间/可信度是合理起点；每条带来源、项目、写入/事件时间、失效状态、预览和全文入口。先用可解释加权排序，再根据评测决定是否增加 MMR/RRF/图检索。
5. **默认保守注入**：按预算注入少量相关背景，明确其优先级低于当前用户与实时工具结果；隔离指令性内容，限制跨项目泄漏，支持禁用、删除和审计。
6. **安全恢复**：阈值提前触发；失败时只提交已验证的压缩结果；每次检查真实 headroom，限定重试次数；provider 原生 payload 必须记录可重放的模型/协议范围。
7. **定义评测而不是只看压缩率**：构建多轮代码任务，测目标/约束/错误码/文件路径/用户偏好召回率、精确引用率、错误记忆率、跨项目泄漏率、工具配对完整率、任务成功率、token/费用、延迟和缓存命中；分别比较原始上下文、滚动摘要、摘要+历史搜索、混合检索。包括中文、跨模型切换、记忆删除和恢复失败等场景。

建议最小落地顺序：**可重建会话日志 → 合法边界压缩 → 历史全文搜索 → 记忆提取与引用 → 混合召回 → 更复杂的图/图片归档**。先保证可恢复、可解释和可测试，再增加压缩策略。

## 8. 关键源码索引

以下路径相对于各自仓库根目录；commit 固定后可用仓库 URL 的 `blob/<commit>/<path>` 定位。

### pi

- `packages/coding-agent/docs/compaction.md`：触发、切点、分支摘要、扩展事件。
- `packages/coding-agent/docs/sessions.md`：会话、树、分叉与上下文。
- `packages/coding-agent/src/core/compaction/compaction.ts`：切点、准备和摘要。
- `packages/coding-agent/src/core/compaction/branch-summarization.ts`：公共祖先与分支摘要。
- `packages/coding-agent/src/core/compaction/utils.ts`：序列化和文件操作跟踪。
- `packages/coding-agent/src/core/session-manager.ts`：session entry、上下文重建。

### oh-my-pi

- `docs/compaction.md`、`docs/memory.md`、`docs/mnemosyne-memory-backend.md`：整体行为和配置。
- `packages/coding-agent/src/session/session-maintenance.ts`、`session-context.ts`、`context-settings.ts`、`compaction-methods.ts`：维护调度、上下文、设置和方法顺序。
- `packages/agent/src/compaction/compaction.ts`、`branch-summarization.ts`、`pruning.ts`、`shake.ts`：主要压缩与裁剪实现。
- `packages/snapcompact/src/snapcompact.ts`：图片归档。
- `packages/coding-agent/src/internal-urls/history-protocol.ts`：历史 URI。
- `packages/coding-agent/src/memory-backend/`、`packages/coding-agent/src/memories/`：后端接入与 local 管线。
- `packages/mnemopi/src/core/beam/recall.ts`、`polyphonic-recall.ts`、`query-cache.ts`、`beam/index.ts`：Mnemopi 检索、融合、缓存和默认权重。
