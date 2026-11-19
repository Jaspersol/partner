# 开发与调试

**简体中文** | [繁體中文](./development.zh-TW.md) | [English](./development.en.md)

返回 [文档目录](./README.md)。

## 架构

| 层 | 技术 | 职责 |
|---|---|---|
| 运行时 | C# / .NET 10 | 会话、队列、回合、记忆、治理、MCP、桌面、语音 |
| 前端 | Vue 3 + Vite + TS | 界面，可在浏览器里单独调试 |
| 桌面壳 | Tauri 2（Rust） | 开窗口、拉起 API、注入令牌 |

三层之间只通过 HTTP + Bearer 令牌连接。所有端点都要求令牌，包括 `/api/health`。

设置读取使用进程内快照；手工修改工作区 `settings/runtime.json` 不会自动生效，重启 API 后读取。启动时文件 JSON 损坏会使用安全默认值，下一次成功保存会重写文件。`GET /api/settings` 返回 `revision`，整表 `PUT /api/settings` 必须携带该版本：缺失返回 400，版本过期返回 409，客户端应保留未保存的编辑并重新读取后处理冲突。提交失败不会推进版本；版本只在当前进程内有效。

`GET /api/consumer` 的工具 `decision` 为 `deny`、`allow`、`ask` 或 `conditional`；`conditional` 表示实际决定取决于调用参数、会话来源或租约，不代表已批准。

## 全量检查

```bash
bash scripts/ci.sh               # 文档引用 → CSS → 编译 → 单测 → smoke + 能力门禁 → 前端 → 壳
SKIP_SHELL=1 bash scripts/ci.sh  # 跳过壳编译
```

只能在 Windows 上跑。

## 分层命令

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ReflexEngine"   # 单个测试类
dotnet run --project src/Partner.Smoke                    # 只跑 smoke，须在仓库根

npm --prefix ui run typecheck
npm --prefix ui run test
npm --prefix ui run build

node scripts/check-docs.mjs                               # 文档引用是否还能解析
node scripts/check-css-names.mjs                          # scoped 类名检查
node scripts/check-engine-pins.mjs                        # 引擎地址与哈希是否仍等于已发布资产，需 gh 登录
node scripts/check-live2d-bundle.mjs                      # 构建产物是否带 Live2D 版权声明（须先 build）
node scripts/e2e-shell.mjs                                # 壳 e2e，需要桌面会话，不在 CI

cargo build --manifest-path src-tauri/Cargo.toml          # 只编译壳
```

## 前端依赖审计与 Live2D 补丁

2026-11-17 按更新后的锁文件运行 `npm audit`：5 项 moderate，均为开发依赖链；`npm audit --omit=dev` 为 0。此前记录为 11 项（8 moderate、3 critical）；审计结果随数据库变化，不代表安装包已证实存在生产 RCE。前端开发与 CI 使用 Node 22.12 及以上的 22.x，或 Vitest 5 支持的其他 Node 版本（`^24.0.0 || >=26.0.0`）。

| 依赖链 | 处置与剩余风险 |
|---|---|
| `vitest` / `@vitest/mocker` / `tinypool` | 升至 Vitest 和 mocker 5.1.0，新的 Vitest 不再依赖 Tinypool，锁文件不再报告此前的 [mock 路径遍历](https://github.com/advisories/GHSA-82fw-gwwq-j7x9)、[mock 注入](https://github.com/advisories/GHSA-qxg6-89mg-rqv8)、Tinypool [原型污染](https://github.com/advisories/GHSA-5gmw-xhrv-c9v3)和[选项注入](https://github.com/advisories/GHSA-85c8-ppgw-ccpr)。仍只运行可信仓库代码，不向不可信网络暴露测试服务。 |
| `happy-dom` | 升至 21.0.4，锁文件不再报告此前的 [VM 逃逸](https://github.com/advisories/GHSA-37j7-fg3j-429f)、[跨域凭据](https://github.com/advisories/GHSA-w4gp-fjgq-3q4g)、[脚本注入](https://github.com/advisories/GHSA-6q6h-j7hj-3r64)、[SSRF](https://github.com/advisories/GHSA-c8p4-j97h-3vg8)和 [cookie 作用域伪造](https://github.com/advisories/GHSA-5fpq-26gf-qwcr)。测试环境仍不是不可信 HTML 或脚本的安全沙箱。 |
| `vue-tsc` → `@vue/language-core` → `minimatch` | 升至 vue-tsc / language-core 3.3.14，锁文件中该链不再报告 [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249)。 |
| `@vue/test-utils` 2.6.0 → `js-beautify` 2.0.3 → `editorconfig` 3.0.2 / `glob` 13.0.7 → `minimatch` 10.2.6 | 剩余 5 项 moderate 是这条链及其受影响包：[minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249)、[glob 取消遍历](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4)。上游当前版本仍依赖受影响的范围；不强行覆盖跨主版本传递依赖，避免破坏工具契约。仅在可信工作树运行，避免不可信 glob 输入；后续跟踪 test-utils、js-beautify、editorconfig 与 glob 的兼容修复，更新锁文件后重跑审计和测试。 |

升级后的行为兼容性仍须通过干净 `npm ci`、类型检查、前端测试和构建验证；不要用 `npm audit fix --force` 强行跨主版本。

`easy-live2d` 固定 1.1.0，由 `patch-package --error-on-fail` 应用 `ui/patches/easy-live2d+1.1.0.patch`，不能改用运行时 monkeypatch。已向上游提交物理前钩子建议：[Panzer-Jack/easy-live2d#55](https://github.com/Panzer-Jack/easy-live2d/issues/55)；上游尚未合并等价实现。建议的回调在 motion / expression / effects 之后、pose / physics 之前，提供逐帧参数读写与 motion 状态，支持加载前注册及传 null 注销。

升级该库时检查 ESM、CJS 和两套类型声明的补丁；干净 `npm ci` 必须成功应用补丁，再运行类型检查、前端测试、构建及真实物理链模型验证，确认程序化叠加仍在 effects 后、physics 前并驱动物理。只有正式版本提供等价接口并迁移调用后，才删除补丁及不再需要的 `patch-package`。

## 会话投影基准

`tools/SessionProjectionBenchmark` 生成确定性的 UTF-8 会话日志，并分别测量打开、首次投影、追加后的投影和进程峰值工作集。每次测量会追加一条记录，必须从同一原始数据集复制新目录，在独立进程中运行；分别记录 10 万和 100 万条，重复多次并注明机器、运行时、磁盘和缓存条件。不要在真实用户工作区运行。

```bash
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-100k 100000
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-1m 1000000
cp -R /tmp/partner-bench-100k /tmp/partner-bench-run
dotnet run -c Release --project tools/SessionProjectionBenchmark -- measure /tmp/partner-bench-run
```

比较修改前后时，将同一基准工具复制到基线工作树，在两个工作树中分别运行，并各自使用原始数据集的新副本。启动耗时包含日志修复检查和索引重建，峰值是整个测量进程的工作集，不是单个阶段的分配量。

本轮测量（时间单位 ms；峰值为字节）：

| 版本 | 初始事件数 | 打开 | 首次投影 | 追加后投影 | 峰值工作集 |
|---|---:|---:|---:|---:|---:|
| 优化前 | 100000 | 210.95 | 243.22 | 33.71 | 104202240 |
| 优化后 | 100000 | 278.86 | 264.83 | 0.76 | 85458944 |
| 优化前 | 1000000 | 1240.16 | 1182.26 | 250.34 | 532164608 |
| 优化后 | 1000000 | 1173.75 | 1304.74 | 0.67 | 413970432 |

测量环境和生成数据的初始事件数相同，但这些是单次采样，仍需重复测量以评估波动。结果显示追加后投影耗时和峰值工作集下降，首次投影耗时上升；打开耗时在两个规模下变化方向不同。普通追加增量构建不可变分块视图；编辑、撤回或潜在前向引用仍完整重放，物理删除重建缓存。

## 只起 API

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

第一个参数是工作区，第二个是端口。API 会把工作区转换成绝对路径；Windows 启动参数可用 `/`，但文件工具的目标路径仍须使用 `\`。不要加 `--urls`，它会被当成工作区路径。没提供令牌时，启动会在 stdout 打印一次：

```
partner-api listening on http://127.0.0.1:5280
token: <base64url>
```

令牌也可以通过环境变量 `PARTNER_API_TOKEN` 传入，不走命令行。

```bash
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/session
```

## 只起前端

```bash
npm --prefix ui run dev          # http://localhost:5173
```

在浏览器控制台写入令牌和端口（前端不接受 URL 参数）：

```js
localStorage.setItem('partner.token', '<token>')
localStorage.setItem('partner.port', '5280')
```

## 能力门禁

能力等级由实跑的 smoke 检查推导，基线在 `capabilities/manifest.baseline.json`。能力消失或降级即失败。新增 smoke 检查后：

```bash
dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json
```

`PARTNER_DESKTOP_LIVE=1` 才会跑会移动焦点的两项桌面检查；不开时记为“未验证”，不算失败。

## 常见坑

- 前端用 npm，不用 pnpm。
- 改完 API 要重新发布再打包，`scripts/build-shell.sh` 已按顺序处理。
- 跑 smoke 前停掉应用：多一个 WebView2 窗口会让桌面观察明显变慢，定时敏感的检查会整片失败。应用在跑时 `cargo build` 也会因文件被占用失败。
- 桌面测试必须串行，它们读的是同一个真实桌面。
- 不要同时跑两轮 `dotnet test` 或 smoke，会抢临时端口。
- `npm run preview` 跑在 4173，已在 CORS 白名单里；新增本地来源要改 `ApiHost.cs`。
