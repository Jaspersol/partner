# 开发与调试

**简体中文** | [繁體中文](./development.zh-TW.md) | [English](./development.en.md)

返回 [文档目录](./README.md)。

## 架构

| 层 | 技术 | 职责 |
|---|---|---|
| 运行时 | C# / .NET 8 | 会话、队列、回合、记忆、治理、MCP、桌面、语音 |
| 前端 | Vue 3 + Vite + TS | 界面，可在浏览器里单独调试 |
| 桌面壳 | Tauri 2（Rust） | 开窗口、拉起 API、注入令牌 |

三层之间只通过 HTTP + Bearer 令牌连接。所有端点都要求令牌，包括 `/api/health`。

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
node scripts/e2e-shell.mjs                                # 壳 e2e，需要桌面会话，不在 CI

cargo build --manifest-path src-tauri/Cargo.toml          # 只编译壳
```

## 只起 API

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

第一个参数是工作区，第二个是端口。不要加 `--urls`，它会被当成工作区路径。没提供令牌时，启动会在 stdout 打印一次：

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
