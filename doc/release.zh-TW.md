# 發佈流程

**繁體中文** | [简体中文](./release.md) | [English](./release.en.md)

返回 [文件索引](./README.zh-TW.md)。

## 一句話

只有一種 tag：打 `v*` 發安裝包。語音引擎不在這個流程裡發佈，安裝包固定下載已發佈的 `engines-v1` release。

## 發佈什麼

| 資產 | 誰產出 | 放在哪 |
|---|---|---|
| `Partner_<版本>_x64-setup.exe`、`<安裝包名>.exe.sha256` | `.github/workflows/release.yml` 的 `app` job | 應用 release（tag `vX.Y.Z`） |
| `SHA256SUMS.txt`、`THIRD-PARTY-NOTICES.md` | 同上 | 同上 |

安裝包裡**不含**引擎：裝完在「設定 → 本地引擎」點下載，位址與 SHA-256 來自 `src/Partner.Api/Voice/EnginePackage.cs` 裡已提交的常數，指向 `engines-v1` release。安裝包只為 `engines/tts/`、`engines/kws-asr/` 建好目錄，並放進去兩個服務腳本；`tauri.conf.json` 裡 `bundle.resources` 的 map 形式把**值當作完整目標路徑**（不是目錄），所以值必須寫成 `engines/tts/tts-server.py`——寫成 `engines/tts/` 會把腳本落成名為 `tts` 的檔案，佔掉引擎目錄的位置。

## 應用內更新

「設定 → 一般」顯示的版本號來自 `ui/package.json`（workflow 已按 tag 寫入）。檢查更新有兩條路徑，倉庫常數都在 `src/Partner.Api/Updates/UpdateService.cs`：

- **API 主路徑**：`api.github.com/repos/Jaspersol/partner/releases/latest` 的 release JSON 給出版本號、安裝包名、大小、下載位址，以及 `<安裝包名>.exe.sha256` 校驗檔。
- **直鏈備援**：完全不碰 REST API。`releases/latest` 的 302 指向 `releases/tag/<tag>`（版本號從這裡取），`SHA256SUMS.txt` 給出安裝包名和 SHA-256，資產從 `releases/latest/download/<檔名>` 或 `releases/download/<tag>/<檔名>` 取。`api.github.com` 每 IP 每小時只有 60 次配額：被共用出口 IP 用滿（API 回傳 403、429 或帶 `Retry-After` 的 403）、網路不通、或 release 少了 `.exe.sha256` 時，檢查會自動切到這條路，並在結果裡以 `source: direct` 標明；失敗後短時間內不再重試 API。

兩條路都必須拿到校驗值，缺 `.exe.sha256` 且缺 `SHA256SUMS.txt` 時不提供更新。下載依 `browser_download_url` → API 資產位址 → 帶 tag 的直鏈 → `releases/latest/download/` 直鏈依序嘗試，任一位址可用即可：候選位址與重導向目標都限制在 GitHub 網域，串流下載有大小上限，完成後檢查 MZ 頭與 SHA-256，通過後才交給殼程式。

所以每個應用 release 必須保留三個資產：`Partner_<版本>_x64-setup.exe`、`<安裝包名>.exe.sha256`、以及列出它的 `SHA256SUMS.txt`。舊 release 只有後兩者仍能更新（走備援路徑），但兩條路都拿不到校驗值時不會提供更新。

下載完成後先核對 SHA-256，再啟動安裝程式並結束應用。安裝程式以 `/UPDATE /P` 執行：**不解除安裝舊版本**，直接覆寫原安裝目錄裡的 `api/`、殼程式與兩個引擎腳本，避開 Tauri 維護頁預設的「先解除安裝再安裝」；`engines/` 下已下載的執行環境與模型不在安裝包的清單裡，所以更新不會動到它們，也不需要重新下載。fork 需要把 `UpdateService.Repository` 改成自己的倉庫。

## 觸發方式

| 方式 | 做什麼 |
|---|---|
| push tag `v0.2.0` | 編譯安裝包並發 release |
| Actions → release → Run workflow，`tag` 留空 | 只編譯，產物進 Actions artifact，不發 release |
| 同上，填 `tag` + 勾 `publish` | 按指定 tag 發安裝包 |

版本號由 tag 決定：workflow 用 `scripts/set-version.mjs` 把 `0.2.0` 寫進 `src-tauri/tauri.conf.json`、`Cargo.toml`、`ui/package.json`，所以 tag 和安裝包版本不會走偏。

## 引擎包

workflow 不打也不發引擎包，所以不需要 self-hosted runner。`engines-v1` 是唯讀的：已發佈的安裝包 pin 了它底下檔案的 SHA-256，改動任何位元組都會讓下載在最後校驗失敗。

這些常數沒人再自動生成，所以每次建置（以及本機）都會跑 `scripts/check-engine-pins.mjs` 核對：它讀 release 資產在 GitHub 上記錄的 SHA-256 digest（不下載 3.4GB），常數打錯或資產被換都會當場失敗，而不是發一個誰也裝不上引擎的安裝包。

真要換引擎時，在裝有 `engines/` 的機器上手動打包到新 tag，再把 `scripts/pack-engines.ps1` 印出的位址表貼進 `EnginePackage.cs`：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -UploadTag engines-v2
```

## 分支與保護

| 分支 | 用途 |
|---|---|
| `master` | 預設分支、受保護：只能透過 PR 合入，禁止強推、禁止刪除，要求線性歷史 |
| `dev` | 日常開發與試跑 |

發佈 tag 打在 `master` 上：`git checkout master && git pull && git tag v0.2.0 && git push github v0.2.0`。

squash 合併會讓兩邊歷史分叉：同一批檔案在 `master` 和 `dev` 上都算「新增」，下一個 PR 會變成 add/add 衝突。合併完把 `dev` 對齊一次：

```bash
git fetch github
git reset --hard github/master
```

## 本機等價指令

workflow 做的每一步都能在本機跑：

```bash
node scripts/check-docs.mjs
node scripts/check-css-names.mjs
node scripts/check-engine-pins.mjs   # 需要 gh 已登入
npm --prefix ui ci
node scripts/set-version.mjs v0.2.0
dotnet build Partner.sln -c Release
dotnet publish src/Partner.Api -c Release -r win-x64 --self-contained true \
  -o src/Partner.Api/bin/Release/net10.0/win-x64/publish
npm --prefix ui exec tauri build
```

產出的安裝包在 `src-tauri/target/release/bundle/nsis/`。

## 已知

- 發佈前會跑單元測試、前端單元/契約測試和 `Partner.Smoke --verify-baseline`；任一失敗會終止 job。舊的 `continue-on-error` 豁免已移除。
- `tauri.conf.json` 的 `bundle.targets` 只有 `nsis`，所以只出 exe 安裝包，沒有 msi。
- 引擎包超大（最大 1.16GB），單資產仍在 GitHub 的 2GiB 上限內；再大就必須繼續切包。
