# 开发与调试

**简体中文** | [繁體中文](./development.zh-TW.md) | [English](./development.en.md)

返回 [文档目录](./README.md)。

## 架构

| 层 | 技术 | 职责 |
|---|---|---|
| 运行时 | C# / .NET 10 | 会话、队列、回合、记忆、治理、MCP、桌面和语音 |
| 前端 | Vue 3 + Vite + TS | 用户界面，也可以直接在浏览器中单独调试 |
| 桌面壳 | Tauri 2（Rust） | 打开窗口、启动 API、注入令牌 |

三层之间只通过带 Bearer 令牌的 HTTP 通信。包括 `/api/health` 在内的所有端点都要求令牌。

设置读取使用进程内快照。手动编辑工作区中的 `settings/runtime.json` 不会自动应用，必须重启 API 才会重新读取。启动时如果 JSON 损坏，程序会退回安全默认值，并在下一次成功保存时重写文件。`GET /api/settings` 返回 `revision`；整份配置执行 `PUT /api/settings` 时必须带上该版本：缺少版本返回 400，版本过期返回 409。客户端应保留未保存的编辑，重新读取后再处理冲突。提交失败不会推进版本号，版本只在当前进程中有效。

`GET /api/consumer` 返回的工具 `decision` 只能是 `deny`、`allow`、`ask` 或 `conditional`。`conditional` 表示最终决定还要看调用参数、会话来源或租约，并不代表已经批准。

## 全量检查

```bash
bash scripts/ci.sh               # 文档引用 → CSS → 编译 → 单测 → smoke + 能力门禁 → 前端 → 壳
SKIP_SHELL=1 bash scripts/ci.sh  # 跳过桌面壳编译
```

只能在 Windows 上执行。

## 分层命令

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ReflexEngine"   # 只运行一个测试类
dotnet run --project src/Partner.Smoke                    # 只运行 smoke，必须在仓库根目录执行

npm --prefix ui run typecheck
npm --prefix ui run test
npm --prefix ui run build

node scripts/check-docs.mjs                               # 检查文档引用是否仍可解析
node scripts/check-css-names.mjs                          # 检查 scoped 类名
node scripts/check-engine-pins.mjs                        # 检查引擎地址和哈希是否仍匹配已发布资源，需要 gh 登录
node scripts/check-live2d-bundle.mjs                      # 检查构建产物是否带 Live2D 版权声明，须先 build
node scripts/e2e-shell.mjs                                # 桌面壳 e2e，需要桌面会话，不在 CI 中运行

cargo build --manifest-path src-tauri/Cargo.toml          # 只编译桌面壳
```

## 前端依赖审计与 Live2D 补丁

2026-11-17 使用更新后的锁文件运行 `npm audit`：发现 5 项 moderate，全部位于开发依赖链；`npm audit --omit=dev` 结果为 0。此前记录为 11 项（8 moderate、3 critical）。审计数量会随 advisory 数据库变化，因此不能据此证明安装包已经存在生产环境 RCE。前端开发和 CI 使用 Node 22.12 或更高的 22.x，或者 Vitest 5 支持的其他 Node 版本（`^24.0.0 || >=26.0.0`）。

| 依赖链 | 处理方式与剩余风险 |
|---|---|
| `vitest` / `@vitest/mocker` / `tinypool` | 已升级到 Vitest 和 mocker 5.1.0；新版 Vitest 不再依赖 Tinypool，锁文件也不再报告之前的 [mock 路径遍历](https://github.com/advisories/GHSA-82fw-gwwq-j7x9)、[mock 注入](https://github.com/advisories/GHSA-qxg6-89mg-rqv8)、Tinypool [原型污染](https://github.com/advisories/GHSA-5gmw-xhrv-c9v3) 和 [选项注入](https://github.com/advisories/GHSA-85c8-ppgw-ccpr)。仍然只运行可信仓库代码，不把测试服务暴露给不可信网络。 |
| `happy-dom` | 已升级到 21.0.4；锁文件不再报告之前的 [VM 逃逸](https://github.com/advisories/GHSA-37j7-fg3j-429f)、[跨域凭据](https://github.com/advisories/GHSA-w4gp-fjgq-3q4g)、[脚本注入](https://github.com/advisories/GHSA-6q6h-j7hj-3r64)、[SSRF](https://github.com/advisories/GHSA-c8p4-j97h-3vg8) 和 [cookie 作用域伪造](https://github.com/advisories/GHSA-5fpq-26gf-qwcr)。测试环境仍然不能当作运行不可信 HTML 或脚本的安全沙箱。 |
| `vue-tsc` → `@vue/language-core` → `minimatch` | 已升级到 vue-tsc / language-core 3.3.14；锁文件中的这条链不再报告 [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249)。 |
| `@vue/test-utils` 2.6.0 → `js-beautify` 2.0.3 → `editorconfig` 3.0.2 / `glob` 13.0.7 → `minimatch` 10.2.6 | 剩余 5 项 moderate 来自这条链及其受影响的包，包括 [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249) 和 [glob 取消遍历](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4)。上游当前版本仍依赖受影响范围，因此不强行跨主版本覆盖传递依赖，以免破坏工具契约。只在可信工作树中运行，避免不可信 glob 输入；后续继续跟踪 test-utils、js-beautify、editorconfig 和 glob 的兼容修复，锁文件更新后重新运行审计与测试。 |

升级后的行为兼容性仍必须通过干净的 `npm ci`、类型检查、前端测试和构建验证。不要使用 `npm audit fix --force` 强行跨主版本升级。

`easy-live2d` 固定为 1.1.0，由 `patch-package --error-on-fail` 应用 `ui/patches/easy-live2d+1.1.0.patch`，不能换成运行时 monkeypatch。已向上游提出物理前钩子建议：[Panzer-Jack/easy-live2d#55](https://github.com/Panzer-Jack/easy-live2d/issues/55)，但上游还没有合并等价实现。建议的回调位于 motion / expression / effects 之后、pose / physics 之前，可以逐帧读写参数并取得 motion 状态，也支持在加载前注册以及传入 null 注销。

升级该库时要同时检查 ESM、CJS 及两套类型声明对应的补丁。干净执行 `npm ci` 必须能成功套用补丁；之后还要运行类型检查、前端测试、构建，以及使用真实物理链模型验证，确认程序化叠加仍处于 effects 之后、physics 之前，并且确实能驱动物理。只有正式版本提供等价接口且调用方完成迁移，才可以删除补丁和不再需要的 `patch-package`。

## 会话投影基准

`tools/SessionProjectionBenchmark` 会生成确定性的 UTF-8 会话日志，并分别测量打开、第一次投影、追加后的投影和进程峰值工作集。每次测量都会追加一条记录，所以必须从同一份原始数据集复制新的目录，并在独立进程中运行。10 万条和 100 万条要分别记录，多次重复时注明机器、运行时、磁盘和缓存条件。不要在真实用户工作区运行。

```bash
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-100k 100000
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-1m 1000000
cp -R /tmp/partner-bench-100k /tmp/partner-bench-run
dotnet run -c Release --project tools/SessionProjectionBenchmark -- measure /tmp/partner-bench-run
```

比较修改前后时，把同一个基准工具复制到基线工作树，在两个工作树中分别运行，并给每次运行使用原始数据集的新副本。启动耗时包含日志修复检查和索引重建；峰值代表整个测量进程的工作集，不是某一个阶段的分配量。

普通追加会增量构建不可变分块视图；编辑、撤回或潜在的前向引用仍然需要完整重放，物理删除会重建缓存。

## 只启动 API

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

第一个参数是工作区，第二个是端口。API 会把工作区转换成绝对路径；Windows 启动参数可以使用 `/`，但文件工具的目标路径仍必须使用 `\`。不要添加 `--urls`，它会被当作工作区路径。未提供令牌时，程序启动时只会向 stdout 打印一次：

```
partner-api listening on http://127.0.0.1:5280
token: <base64url>
```

也可以通过环境变量 `PARTNER_API_TOKEN` 提供令牌，不要把令牌放在命令行中。

```bash
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/session
```

## 只启动前端

```bash
npm --prefix ui run dev          # http://localhost:5173
```

在浏览器控制台中写入端口和令牌（前端不接受 URL 参数）：

```js
localStorage.setItem('partner.token', '<token>')
localStorage.setItem('partner.port', '5280')
```

## 能力门禁

能力等级由实际执行的 smoke 检查推导，基线文件是 `capabilities/manifest.baseline.json`。能力消失或等级下降都会使门禁失败。新增 smoke 检查后运行：

```bash
dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json
```

只有设置 `PARTNER_DESKTOP_LIVE=1` 才会运行会移动焦点的两项桌面检查；未设置时它们记为“未验证”，而不是失败。

## 常见问题

- 前端使用 npm，不使用 pnpm。
- 修改 API 后必须先重新发布再打包，`scripts/build-shell.sh` 已按正确顺序处理。
- 运行 smoke 前先关闭应用：多出的 WebView2 窗口会明显拖慢桌面观察，时间敏感的检查可能一起失败。应用运行时执行 `cargo build` 也可能因文件被占用而失败。
- 桌面测试必须串行运行，因为它们读取的是同一个真实桌面。
- 不要同时运行两轮 `dotnet test` 或 smoke，它们会争用临时端口。
- `npm run preview` 使用 4173 端口，该端口已在 CORS 白名单中；新增本地来源时要修改 `ApiHost.cs`。
