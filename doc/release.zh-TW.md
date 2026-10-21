# 發佈流程

**繁體中文** | [简体中文](./release.md) | [English](./release.en.md)

返回 [文件索引](./README.zh-TW.md)。

## 一句話

打一個 `v*` tag，GitHub Actions 編譯安裝包，把引擎權重包和安裝包一起發到 release。

## 發佈什麼

| 資產 | 誰產出 | 放在哪 |
|---|---|---|
| `Partner_<版本>_x64-setup.exe` | `.github/workflows/release.yml` 的 `app` job | 應用 release（tag `vX.Y.Z`） |
| `SHA256SUMS.txt`、`engines-manifest.json`、`THIRD-PARTY-NOTICES.md` | 同上 | 同上 |
| `partner-engine-tts-a-runtime.zip`、`-b-talker`、`-c-codec`、`partner-engine-kws.zip` | `scripts/pack-engines.ps1` | 引擎 release（tag `engines-vN`） |

安裝包裡**不含**引擎：裝完在「設定 → 本機引擎」按下載，位址與 SHA-256 在編譯期就固定好了。

## 觸發方式

| 方式 | 做什麼 |
|---|---|
| push tag `v0.2.0` | 編譯安裝包並發 release；**引擎權重包不會重打**（除非開了 self-hosted runner，見下） |
| Actions → release → Run workflow，`tag` 留空 | 只編譯，產物進 Actions artifact，不發 release |
| 同上，填 `tag` + 勾 `publish` | 按指定 tag 發佈 |

版本號由 tag 決定：workflow 用 `scripts/set-version.mjs` 把 `0.2.0` 寫進 `src-tauri/tauri.conf.json`、`Cargo.toml`、`ui/package.json`，所以 tag 和安裝包版本不會走偏。

## 引擎包：為什麼預設只在本機打

權重和 venv 不進 git（`engines/` 只有兩個服務腳本），GitHub 的託管 runner 上沒有 2.3G 模型，**打不出引擎包**。所以：

- **預設**：在裝有 `engines/` 的開發機上跑一次

  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts/pack-engines.ps1 -UploadTag engines-v1
  ```

  它會重打可搬移的 Python 執行環境、切包、算 SHA-256、寫 `NOTICE/`、上傳到 `engines-v1`，並印出要貼進 `EnginePackage.cs` 的位址表。

- **自動化**：註冊一台 self-hosted runner，標籤 `self-hosted, windows, x64`，機器上有 `gh`、`engines/tts/model`，再設倉庫變數 `ENGINE_RUNNER=true`。workflow 的 `engines` job 就會在打 tag 時先打包上傳，`app` job 接著編譯；倉庫變數 `ENGINE_TARGET_BRANCH`（預設 `master`）決定引擎 tag 建在哪個分支上。

引擎更新開新 tag（`engines-v2`），不要覆蓋舊 tag：已經裝了舊包的機器半途重試會 SHA-256 不一致。

## 安裝包怎麼鎖定引擎包

`app` job 會：

1. `gh release download <引擎 tag> --pattern engines-manifest.json`（引擎 tag 預設取倉庫變數 `ENGINE_TAG`，沒設就是 `engines-v1`；手動觸發時也可用輸入參數覆蓋）；
2. `node scripts/apply-engines-manifest.mjs dist/engines/engines-manifest.json --check` 先比對，不一致只發一條 **warning**（提示你同步常數）；
3. 再跑一次不帶 `--check` 的同一腳本，**把清單裡的位址和 SHA-256 寫進 `EnginePackage.cs` 再編譯**。

所以安裝包裡的 hash 永遠等於引擎 release 上的實際資產，不會因為忘了手改常數而發錯；差別只是「倉庫裡的常數有沒有跟上」，這個會以 CI warning 提醒。

引擎包換了 tag（`engines-v2`）時，改倉庫變數 `ENGINE_TAG=engines-v2` 就行，不用改 workflow。

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
npm --prefix ui ci
node scripts/apply-engines-manifest.mjs dist/engines/engines-manifest.json --check
node scripts/set-version.mjs v0.2.0
dotnet build Partner.sln -c Release
dotnet publish src/Partner.Api -c Release -r win-x64 --self-contained true \
  -o src/Partner.Api/bin/Release/net8.0/win-x64/publish
npm --prefix ui exec tauri build
```

產出的安裝包在 `src-tauri/target/release/bundle/nsis/`。

## 已知

- 單元測試步驟標了 `continue-on-error`：倉庫裡有兩個與發佈無關的既有失敗（`AgentsEndpointTests.The_round_count_and_the_prompt_have_limits`、`SettingsEndpointTests.Growing_the_context_does_not_compact`），修好後可以去掉這個豁免。
- `tauri.conf.json` 的 `bundle.targets` 只有 `nsis`，所以只出 exe 安裝包，沒有 msi。
- 引擎包超大（最大 1.16GB），單資產仍在 GitHub 的 2GiB 上限內；再大就必須繼續切包。
