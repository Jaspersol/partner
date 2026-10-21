# 開發與除錯

[简体中文](./development.md) | **繁體中文** | [English](./development.en.md)

返回 [文件目錄](./README.zh-TW.md)。

## 架構

| 層 | 技術 | 職責 |
|---|---|---|
| 執行階段 | C# / .NET 8 | 工作階段、佇列、回合、記憶、治理、MCP、桌面、語音 |
| 前端 | Vue 3 + Vite + TS | 介面，可在瀏覽器裡單獨除錯 |
| 桌面殼 | Tauri 2（Rust） | 開視窗、帶起 API、注入權杖 |

三層之間只透過 HTTP + Bearer 權杖連接。所有端點都要求權杖，包括 `/api/health`。

## 完整檢查

```bash
bash scripts/ci.sh               # 文件引用 → CSS → 編譯 → 單元測試 → smoke + 能力門檻 → 前端 → 殼
SKIP_SHELL=1 bash scripts/ci.sh  # 跳過殼編譯
```

只能在 Windows 上跑。

## 分層指令

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ReflexEngine"   # 單一測試類別
dotnet run --project src/Partner.Smoke                    # 只跑 smoke，須在儲存庫根目錄

npm --prefix ui run typecheck
npm --prefix ui run test
npm --prefix ui run build

node scripts/check-docs.mjs                               # 文件引用是否還能解析
node scripts/check-css-names.mjs                          # scoped 類別名稱檢查
node scripts/check-engine-pins.mjs                        # 引擎位址與雜湊是否仍等於已發佈資產，需 gh 登入
node scripts/e2e-shell.mjs                                # 殼 e2e，需要桌面工作階段，不在 CI

cargo build --manifest-path src-tauri/Cargo.toml          # 只編譯殼
```

## 只起 API

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

第一個參數是工作區，第二個是連接埠。不要加 `--urls`，它會被當成工作區路徑。沒提供權杖時，啟動會在 stdout 印出一次：

```
partner-api listening on http://127.0.0.1:5280
token: <base64url>
```

權杖也可以透過環境變數 `PARTNER_API_TOKEN` 傳入，不走命令列。

```bash
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/session
```

## 只起前端

```bash
npm --prefix ui run dev          # http://localhost:5173
```

在瀏覽器主控台寫入權杖和連接埠（前端不接受 URL 參數）：

```js
localStorage.setItem('partner.token', '<token>')
localStorage.setItem('partner.port', '5280')
```

## 能力門檻

能力等級由實際執行的 smoke 檢查推導，基準在 `capabilities/manifest.baseline.json`。能力消失或降級即失敗。新增 smoke 檢查後：

```bash
dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json
```

`PARTNER_DESKTOP_LIVE=1` 才會跑會移動焦點的兩項桌面檢查；不開時記為「未驗證」，不算失敗。

## 常見陷阱

- 前端用 npm，不用 pnpm。
- 改完 API 要重新發佈再打包，`scripts/build-shell.sh` 已依序處理。
- 跑 smoke 前關掉應用程式：多一個 WebView2 視窗會讓桌面觀察明顯變慢，時間敏感的檢查會整片失敗。應用程式在跑時 `cargo build` 也會因檔案被佔用而失敗。
- 桌面測試必須循序執行，它們讀的是同一個真實桌面。
- 不要同時跑兩輪 `dotnet test` 或 smoke，會搶臨時連接埠。
- `npm run preview` 跑在 4173，已在 CORS 白名單裡；新增本機來源要改 `ApiHost.cs`。
