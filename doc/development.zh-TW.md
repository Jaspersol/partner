# 開發與除錯

[简体中文](./development.md) | **繁體中文** | [English](./development.en.md)

返回 [文件目錄](./README.zh-TW.md)。

## 架構

| 層 | 技術 | 職責 |
|---|---|---|
| 執行階段 | C# / .NET 10 | 工作階段、佇列、回合、記憶、治理、MCP、桌面、語音 |
| 前端 | Vue 3 + Vite + TS | 介面，可在瀏覽器裡單獨除錯 |
| 桌面殼 | Tauri 2（Rust） | 開視窗、帶起 API、注入權杖 |

三層之間只透過 HTTP + Bearer 權杖連接。所有端點都要求權杖，包括 `/api/health`。

設定讀取使用程序內快照；手動修改工作區 `settings/runtime.json` 不會自動生效，重新啟動 API 後才會讀取。啟動時 JSON 損壞會使用安全預設值，下次成功儲存時重寫檔案。`GET /api/settings` 回傳 `revision`，整份 `PUT /api/settings` 必須帶上該版本：缺少時回傳 400，過期時回傳 409；用戶端應保留未儲存的編輯，重新讀取後處理衝突。提交失敗不會推進版本；版本只在目前程序內有效。

`GET /api/consumer` 的工具 `decision` 為 `deny`、`allow`、`ask` 或 `conditional`；`conditional` 表示實際決定取決於呼叫參數、工作階段來源或租約，不代表已批准。

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
node scripts/check-live2d-bundle.mjs                      # 建置產物是否帶 Live2D 版權聲明（須先 build）
node scripts/e2e-shell.mjs                                # 殼 e2e，需要桌面工作階段，不在 CI

cargo build --manifest-path src-tauri/Cargo.toml          # 只編譯殼
```

## 前端依賴稽核與 Live2D 修補

2026-11-17 依更新後的鎖定檔執行 `npm audit`：5 項 moderate，皆為開發依賴鏈；`npm audit --omit=dev` 為 0。先前記錄為 11 項（8 moderate、3 critical）；稽核結果隨資料庫變化，不代表安裝包已證實存在生產環境 RCE。前端開發與 CI 使用 Node 22.12 及以上的 22.x，或 Vitest 5 支援的其他 Node 版本（`^24.0.0 || >=26.0.0`）。

| 依賴鏈 | 處置與剩餘風險 |
|---|---|
| `vitest` / `@vitest/mocker` / `tinypool` | 升至 Vitest 與 mocker 5.1.0，新的 Vitest 不再依賴 Tinypool，鎖定檔不再回報先前的 [mock 路徑遍歷](https://github.com/advisories/GHSA-82fw-gwwq-j7x9)、[mock 注入](https://github.com/advisories/GHSA-qxg6-89mg-rqv8)、Tinypool [原型污染](https://github.com/advisories/GHSA-5gmw-xhrv-c9v3)與[選項注入](https://github.com/advisories/GHSA-85c8-ppgw-ccpr)。仍只執行可信儲存庫程式碼，不向不可信網路暴露測試服務。 |
| `happy-dom` | 升至 21.0.4，鎖定檔不再回報先前的 [VM 逃脫](https://github.com/advisories/GHSA-37j7-fg3j-429f)、[跨來源憑證](https://github.com/advisories/GHSA-w4gp-fjgq-3q4g)、[腳本注入](https://github.com/advisories/GHSA-6q6h-j7hj-3r64)、[SSRF](https://github.com/advisories/GHSA-c8p4-j97h-3vg8)與 [cookie 作用範圍偽造](https://github.com/advisories/GHSA-5fpq-26gf-qwcr)。測試環境仍不是不可信 HTML 或腳本的安全沙箱。 |
| `vue-tsc` → `@vue/language-core` → `minimatch` | 升至 vue-tsc / language-core 3.3.14，鎖定檔中該鏈不再回報 [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249)。 |
| `@vue/test-utils` 2.6.0 → `js-beautify` 2.0.3 → `editorconfig` 3.0.2 / `glob` 13.0.7 → `minimatch` 10.2.6 | 剩餘 5 項 moderate 是這條鏈及其受影響套件：[minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249)、[glob 取消遍歷](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4)。上游目前版本仍依賴受影響的範圍；不強制覆寫跨主版本的傳遞依賴，避免破壞工具契約。僅在可信工作樹執行，避免不可信的 glob 輸入；後續追蹤 test-utils、js-beautify、editorconfig 與 glob 的相容修正，更新鎖定檔後重跑稽核與測試。 |

升級後的行為相容性仍須通過乾淨的 `npm ci`、型別檢查、前端測試與建置驗證；不要用 `npm audit fix --force` 強行跨主版本。

`easy-live2d` 固定 1.1.0，由 `patch-package --error-on-fail` 套用 `ui/patches/easy-live2d+1.1.0.patch`，不能改用執行階段 monkeypatch。已向上游提交物理前鉤子建議：[Panzer-Jack/easy-live2d#55](https://github.com/Panzer-Jack/easy-live2d/issues/55)；上游尚未合併等價實作。建議的回呼在 motion / expression / effects 之後、pose / physics 之前，提供逐幀參數讀寫與 motion 狀態，支援載入前註冊及傳 null 註銷。

升級該程式庫時檢查 ESM、CJS 與兩套型別宣告的修補；乾淨的 `npm ci` 必須成功套用修補，再執行型別檢查、前端測試、建置及真實物理鏈模型驗證，確認程序化疊加仍在 effects 後、physics 前並驅動物理。只有正式版本提供等價介面並遷移呼叫後，才刪除修補及不再需要的 `patch-package`。

## 工作階段投影基準

`tools/SessionProjectionBenchmark` 產生確定性的 UTF-8 工作階段日誌，並分別量測開啟、首次投影、附加後的投影與行程峰值工作集。每次量測會附加一筆記錄，必須從同一原始資料集複製新目錄，在獨立行程中執行；分別記錄 10 萬與 100 萬筆，重複多次並註明機器、執行階段、磁碟與快取條件。不要在真實使用者工作區執行。

```bash
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-100k 100000
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-1m 1000000
cp -R /tmp/partner-bench-100k /tmp/partner-bench-run
dotnet run -c Release --project tools/SessionProjectionBenchmark -- measure /tmp/partner-bench-run
```

比較修改前後時，將同一基準工具複製到基線工作樹，在兩個工作樹中分別執行，並各自使用原始資料集的新副本。啟動耗時包含日誌修復檢查與索引重建，峰值是整個量測行程的工作集，不是單一階段的配置量。

本輪量測（時間單位 ms；峰值為位元組）：

| 版本 | 初始事件數 | 開啟 | 首次投影 | 附加後投影 | 峰值工作集 |
|---|---:|---:|---:|---:|---:|
| 最佳化前 | 100000 | 210.95 | 243.22 | 33.71 | 104202240 |
| 最佳化後 | 100000 | 278.86 | 264.83 | 0.76 | 85458944 |
| 最佳化前 | 1000000 | 1240.16 | 1182.26 | 250.34 | 532164608 |
| 最佳化後 | 1000000 | 1173.75 | 1304.74 | 0.67 | 413970432 |

量測環境與產生資料的初始事件數相同，但這些是單次取樣，仍需重複量測以評估波動。結果顯示附加後投影耗時與峰值工作集下降，首次投影耗時上升；開啟耗時在兩個規模下變化方向不同。一般附加會增量建構不可變分塊視圖；編輯、撤回或潛在前向引用仍完整重放，物理刪除重建快取。

## 只起 API

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

第一個參數是工作區，第二個是連接埠。API 會把工作區轉成絕對路徑；Windows 啟動參數可用 `/`，但檔案工具的目標路徑仍須使用 `\`。不要加 `--urls`，它會被當成工作區路徑。沒提供權杖時，啟動會在 stdout 印出一次：

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
