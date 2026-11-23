# 開發與除錯

[简体中文](./development.md) | **繁體中文** | [English](./development.en.md)

返回 [文件目錄](./README.zh-TW.md)。

## 架構

| 層 | 技術 | 職責 |
|---|---|---|
| 執行階段 | C# / .NET 10 | 工作階段、佇列、回合、記憶、治理、MCP、桌面與語音 |
| 前端 | Vue 3 + Vite + TS | 使用者介面，也能在瀏覽器中單獨除錯 |
| 桌面殼 | Tauri 2（Rust） | 開啟視窗、帶起 API、注入權杖 |

三層只透過帶 Bearer 權杖的 HTTP 通訊。包含 `/api/health` 在內的所有端點都需要權杖。

設定讀取採用程序內快照。手動修改工作區的 `settings/runtime.json` 不會自動套用，必須重新啟動 API 才會讀取。啟動時若 JSON 損壞，會退回安全預設值，下一次成功儲存時再寫回檔案。`GET /api/settings` 會回傳 `revision`；整份設定執行 `PUT /api/settings` 時必須帶上它：缺少版本回傳 400，版本過期回傳 409。用戶端應保留未儲存的編輯，重新讀取後處理衝突。提交失敗不會推進版本，而且版本只在目前程序內有效。

`GET /api/consumer` 中工具的 `decision` 值為 `deny`、`allow`、`ask` 或 `conditional`。`conditional` 代表實際決定仍取決於呼叫參數、工作階段來源或租約，不表示已經批准。

## 完整檢查

```bash
bash scripts/ci.sh               # 文件引用 → CSS → 編譯 → 單元測試 → smoke + 能力門檻 → 前端 → 殼
SKIP_SHELL=1 bash scripts/ci.sh  # 跳過桌面殼編譯
```

只能在 Windows 上執行。

## 分層指令

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ReflexEngine"   # 只執行一個測試類別
dotnet run --project src/Partner.Smoke                    # 只跑 smoke，必須在儲存庫根目錄執行

npm --prefix ui run typecheck
npm --prefix ui run test
npm --prefix ui run build

node scripts/check-docs.mjs                               # 檢查文件引用是否仍能解析
node scripts/check-css-names.mjs                          # 檢查 scoped 類別名稱
node scripts/check-engine-pins.mjs                        # 檢查引擎位址與雜湊是否仍符合已發佈資產，需要 gh 登入
node scripts/check-live2d-bundle.mjs                      # 檢查建置產物是否帶有 Live2D 聲明，須先 build
node scripts/e2e-shell.mjs                                # 桌面殼 e2e，需要桌面工作階段，不在 CI 執行

cargo build --manifest-path src-tauri/Cargo.toml          # 只編譯桌面殼
```

## 前端依賴稽核與 Live2D 修補

2026-11-17 依更新後的鎖定檔執行 `npm audit`：共有 5 項 moderate，全部來自開發依賴鏈；`npm audit --omit=dev` 為 0。先前記錄是 11 項（8 moderate、3 critical）。稽核數量會隨 advisory 資料庫變動，因此不能據此證明安裝包存在生產環境 RCE。前端開發和 CI 使用 Node 22.12 或以上的 22.x，或 Vitest 5 支援的其他 Node 版本（`^24.0.0 || >=26.0.0`）。

| 依賴鏈 | 處理方式與剩餘風險 |
|---|---|
| `vitest` / `@vitest/mocker` / `tinypool` | 已升級到 Vitest 與 mocker 5.1.0；新版 Vitest 不再依賴 Tinypool，鎖定檔也不再回報先前的 [mock 路徑遍歷](https://github.com/advisories/GHSA-82fw-gwwq-j7x9)、[mock 注入](https://github.com/advisories/GHSA-qxg6-89mg-rqv8)、Tinypool [原型污染](https://github.com/advisories/GHSA-5gmw-xhrv-c9v3) 和 [選項注入](https://github.com/advisories/GHSA-85c8-ppgw-ccpr)。仍只執行可信儲存庫程式碼，不把測試服務暴露給不可信網路。 |
| `happy-dom` | 已升級到 21.0.4；鎖定檔不再回報先前的 [VM 逃脫](https://github.com/advisories/GHSA-37j7-fg3j-429f)、[跨來源憑證](https://github.com/advisories/GHSA-w4gp-fjgq-3q4g)、[腳本注入](https://github.com/advisories/GHSA-6q6h-j7hj-3r64)、[SSRF](https://github.com/advisories/GHSA-c8p4-j97h-3vg8) 和 [cookie 範圍偽造](https://github.com/advisories/GHSA-5fpq-26gf-qwcr)。測試環境仍不是執行不可信 HTML 或腳本的安全沙箱。 |
| `vue-tsc` → `@vue/language-core` → `minimatch` | 已升級到 vue-tsc / language-core 3.3.14；鎖定檔中的這條鏈不再回報 [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249)。 |
| `@vue/test-utils` 2.6.0 → `js-beautify` 2.0.3 → `editorconfig` 3.0.2 / `glob` 13.0.7 → `minimatch` 10.2.6 | 剩餘 5 項 moderate 來自這條鏈及其受影響套件，包括 [minimatch DoS](https://github.com/advisories/GHSA-g3hv-75wg-m249) 和 [glob 取消遍歷](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4)。上游目前版本仍依賴受影響範圍，因此不強行跨主版本覆寫傳遞依賴，以免破壞工具契約。只在可信工作樹中執行，避免不可信 glob 輸入；持續追蹤 test-utils、js-beautify、editorconfig 和 glob 的相容修復，更新鎖定檔後重新跑稽核和測試。 |

升級後仍須以乾淨的 `npm ci`、型別檢查、前端測試和建置驗證行為相容性。不要使用 `npm audit fix --force` 強行跨主版本升級。

`easy-live2d` 固定為 1.1.0，由 `patch-package --error-on-fail` 套用 `ui/patches/easy-live2d+1.1.0.patch`，不可改成執行階段 monkeypatch。已向上游提出物理前鉤子建議：[Panzer-Jack/easy-live2d#55](https://github.com/Panzer-Jack/easy-live2d/issues/55)，但上游尚未合併等價實作。建議回呼位於 motion / expression / effects 之後、pose / physics 之前，提供逐影格參數讀寫和 motion 狀態，也支援載入前註冊及傳入 null 解除註冊。

升級此程式庫時，要檢查 ESM、CJS 以及兩套型別宣告的修補。乾淨的 `npm ci` 必須能成功套用補丁，之後再跑型別檢查、前端測試、建置與真實物理鏈模型驗證，確認程式化疊加仍位於 effects 後、physics 前，並且確實驅動物理。只有正式版本提供等價介面且呼叫端完成遷移後，才能刪除修補和不再需要的 `patch-package`。

## 工作階段投影基準

`tools/SessionProjectionBenchmark` 會產生確定性的 UTF-8 工作階段日誌，分別量測開啟、第一次投影、附加後投影和程序峰值工作集。每次量測都會附加一筆記錄，因此必須從同一份原始資料集複製新目錄，並在獨立程序中執行。10 萬和 100 萬筆要分開記錄，重複幾次時註明機器、執行階段、磁碟和快取條件。不要在真實使用者工作區執行。

```bash
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-100k 100000
dotnet run -c Release --project tools/SessionProjectionBenchmark -- generate /tmp/partner-bench-1m 1000000
cp -R /tmp/partner-bench-100k /tmp/partner-bench-run
dotnet run -c Release --project tools/SessionProjectionBenchmark -- measure /tmp/partner-bench-run
```

比較變更前後時，將同一個基準工具複製到基線工作樹，在兩個工作樹中分別執行，並為每次執行使用原始資料集的新副本。啟動時間包含日誌修復檢查和索引重建；峰值是整個量測程序的工作集，不是單一階段的配置量。

一般附加會增量建立不可變分塊視圖；編輯、撤回或可能的前向引用仍需完整重放，而實體刪除會重建快取。

## 只啟動 API

```bash
dotnet run --project src/Partner.Api -- "D:/tmp/ws" 5280
```

第一個參數是工作區，第二個是連接埠。API 會將工作區轉為絕對路徑；Windows 啟動參數可使用 `/`，但檔案工具的目標路徑仍必須使用 `\`。不要加上 `--urls`，它會被當成工作區路徑。沒有提供權杖時，啟動只會在 stdout 印出一次：

```
partner-api listening on http://127.0.0.1:5280
token: <base64url>
```

也可以用環境變數 `PARTNER_API_TOKEN` 傳入權杖，不要放在命令列中。

```bash
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5280/api/session
```

## 只啟動前端

```bash
npm --prefix ui run dev          # http://localhost:5173
```

在瀏覽器主控台寫入權杖與連接埠；前端不接受 URL 參數：

```js
localStorage.setItem('partner.token', '<token>')
localStorage.setItem('partner.port', '5280')
```

## 能力門檻

能力等級取自實際跑過的 smoke 檢查，基準檔為 `capabilities/manifest.baseline.json`。能力消失或降級就會使門檻失敗。新增 smoke 檢查後執行：

```bash
dotnet run --project src/Partner.Smoke -- --update-baseline capabilities/manifest.baseline.json
```

只有設定 `PARTNER_DESKTOP_LIVE=1` 才會執行會移動焦點的兩項桌面檢查；未設定時會標為「未驗證」，不算失敗。

## 常見陷阱

- 前端使用 npm，不使用 pnpm。
- 修改 API 後要先重新發佈再打包；`scripts/build-shell.sh` 已依正確順序處理。
- 執行 smoke 前先關閉應用程式：額外的 WebView2 視窗會明顯拖慢桌面觀察，時間敏感的檢查可能一起失敗。應用程式執行時 `cargo build` 也可能因檔案被鎖定而失敗。
- 桌面測試必須循序執行，因為它們讀取同一個真實桌面。
- 不要同時執行兩輪 `dotnet test` 或 smoke，會競爭臨時連接埠。
- `npm run preview` 使用 4173，該連接埠已在 CORS 白名單中；新增本機來源時要修改 `ApiHost.cs`。
