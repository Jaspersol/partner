# 对外记忆服务（Memory Service）

**简体中文** | [繁體中文](./services.zh-TW.md) | [English](./services.en.md)

返回 [文档目录](./README.md)。

「对外记忆服务」把应用变成可托管多个反向代理实例的宿主：每个实例占用一个独立的 `127.0.0.1` 端口，透传上游 OpenAI 或 Anthropic 模型，在请求里注入主记忆召回，可选地在请求前加一段自定义提示词，并按设定的时间把本服务期间的交互总结成一句话，写回主记忆库。

## 它是什么

- 每个实例是一个反向代理：把客户端的认证换成上游 Key、把对外模型名替换成上游模型名，其余照转发。`tools`、`tool_choice`、`stream`、`response_format` 以及上游返回的 `tool_calls`、SSE、`usage`、错误体都原样透传。
- 服务**没有会话**：客户端自己保存对话历史（无状态），每次请求带完整历史；服务端只在本地暂存待总结的轮次。
- 每次请求会检索主记忆并注入到最后一条含文本的 user 消息前面；可选自定义提示词（留空则不改动请求）。
- 定时（或停机、删除、手动「立即总结」时）把这一窗口的交互总结成一句话写入主记忆库，来源标记 `service:<id>`。
- 只监听 `127.0.0.1`，不暴露到局域网。对外暴露的模型名必须等于配置的模型名，否则返回 404。

## 在界面里创建服务

侧边栏 **服务（Services）** 页：

1. 点「新增」，填写下表字段并保存。保存即启动，实例监听 `http://127.0.0.1:<port>/v1`。
2. 创建时服务端生成 ID 和令牌；**明文令牌只在创建（或轮换）时显示一次**，请复制保存。
3. 卡片上能看到运行状态与错误、请求数、待总结轮数、上次总结时间、最近写入的记忆，并可直接跳转记忆页。
4. 卡片操作：立即总结、轮换令牌（明文只显示一次）、删除（对话框里可选「同时删除它写入的记忆」）。
5. 改端口或协议会重建监听，其余字段热更新，下一个请求即生效。每个端口一个令牌。

## 字段说明

| 字段 | 说明 |
|---|---|
| 名称 | 显示名，也用于摘要的来源标签 |
| 启用 | 关闭即停止该实例 |
| 端口 | 1024–65535，各实例不能重复，也不能是主 API 端口 |
| 协议 | `openai` 或 `anthropic`。**必须与上游协议一致**：透传不做协议转换，不一致时保存会被拒绝（已存在的配置在请求时返回 502） |
| 模型名 | 对外暴露的模型名；请求里的 `model` 必须等于它 |
| 上下文长度 | 用于计算召回注入的 token 预算，默认继承主模型 |
| 自定义提示词 | 可前置一段提示词；留空则不改动请求 |
| 召回 | 开关、条数上限、token 预算；关闭则不注入记忆 |
| 总结 | 开关、间隔（分钟）、最长等待（分钟）；关掉后不再写回主记忆 |
| 上游 | 默认「继承主模型」；也可自定义 baseUrl、模型名、API Key、最大输出 tokens 和超时 |
| 令牌 | 每个端口独立；创建／轮换时只显示一次明文 |

## 管理接口

管理接口挂在主 API 上，沿用启动时的 Bearer 令牌：

- `GET /api/services`、`GET /api/services/{id}`：列出实例及其运行状态。
- `POST /api/services`：创建，响应中**仅此一次**返回明文令牌。
- `PUT /api/services/{id}`：修改（令牌、上游 Key 留空表示保留原值）。
- `DELETE /api/services/{id}?purgeMemories=false`：删除；为 `true` 时同时软删除该服务写入的记忆。
- `POST /api/services/{id}/flush`：立即总结，返回写入的记忆 ID。
- `POST /api/services/{id}/token`：轮换令牌，返回一次明文。

## 调用示例

`model` 必须等于该服务配置的对外模型名。两种认证头对两个协议都可用：`Authorization: Bearer <token>` 或 `x-api-key: <token>`。

OpenAI 协议（`POST /v1/chat/completions`，用 Bearer）：

```bash
curl http://127.0.0.1:<port>/v1/chat/completions -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"model":"<model>","messages":[{"role":"user","content":"..."}]}'
```

Anthropic 协议（`POST /v1/messages`，用 `x-api-key`）：

```bash
curl http://127.0.0.1:<port>/v1/messages -H "x-api-key: <token>" -H "anthropic-version: 2023-06-01" -H "Content-Type: application/json" -d '{"model":"<model>","max_tokens":1024,"messages":[{"role":"user","content":"..."}]}'
```

列出可用模型（`GET /v1/models`）：

```bash
curl http://127.0.0.1:<port>/v1/models -H "Authorization: Bearer <token>"
```

流式（`stream: true`）、工具调用、`response_format` 等参数原样透传，SSE 逐帧返回。

### 超时与转发的响应头

- 等待上游响应头有时间上限（继承主模型时用主模型的超时，自定义上游时可单独设置）；超时返回 `504`，错误体按协议格式。拿到响应头之后，流式传输本身不再限时。非流式请求要等整个回复生成完才有响应头，推理模型的长回复可能因此超时，这种情况请用 `stream: true`。
- 对上游返回的 `retry-after`、`x-ratelimit-*`、`anthropic-ratelimit-*`、`request-id`、`x-request-id`、`openai-processing-ms`、`cache-control` 原样转发，方便 SDK 做退避重试；`set-cookie` 与框架类头（`content-length`、`transfer-encoding`、`connection`）不转发。

## 记忆归属与删除

> **服务记忆写入主记忆库、主会话能召回；删除对话不会删除服务记忆，需要在服务页删除。** 服务把交互总结写进主记忆库（来源 `service:<id>`），主会话之后的回合会像普通记忆一样召回它们。删除或撤回主会话里的对话**不会**级联删除这些记忆。

要清除这些记忆，请在「服务」页删除对应服务，并勾选「同时删除它写入的记忆」（等价于 `DELETE /api/services/{id}?purgeMemories=true`）；不勾选则保留。删除服务时会先停止监听（停止时会做最后一次总结），再删除记忆，避免清除之后又有请求写回一条新的。

## 安全须知

- 外部内容会被总结后写进主记忆，而主会话之后会召回这些摘要，因此存在**间接提示注入**风险：不可信的调用方可能借摘要文字影响主 agent。只让可信的客户端访问服务端口，并定期查看写入的摘要。
- 关闭总结：在「服务」页把「总结」开关关掉（即 `Summary.Enabled=false`）。关闭后服务仍会注入记忆召回，但不再把交互写回主记忆库；已经写入的记忆按上一节的方法手动删除。
- 停机时所有实例并行做最后一次总结。如果有实例在上游超时内没有答完，这一批会用模板句式直接写入（不再等模型），不会因为退出而丢数据。
- 令牌不能为空：保存时会自动为新增实例生成一个，校验要求至少 32 个字符；空令牌的实例一律拒绝所有请求。
- 每个实例只监听 `127.0.0.1`，不会暴露到局域网；端口请求仍需带该实例的令牌，管理接口沿用主 API 的启动令牌。
