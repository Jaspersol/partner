# 快速上手

[简体中文](./getting-started.md) | **繁體中文** | [English](./getting-started.en.md)

返回 [文件目錄](./README.zh-TW.md)。

## 1. 準備環境

| 項目 | 要求 |
|---|---|
| 系統 | Windows 10 2004 以上 |
| .NET | .NET 10 SDK |
| Node | Node 22 + npm。請勿使用 pnpm：它預設會攔截相依套件的建置腳本，導致 esbuild 無法安裝 |
| Rust | cargo 1.98+，只有桌面殼需要 |
| WebView2 | Win10 以上內建 |
| 硬體 | 語音需要麥克風和喇叭；本機 TTS 需要顯示卡 |

## 2. 啟動

```bash
npm --prefix ui install          # 第一次執行
npx --prefix ui tauri dev        # 開發模式，Vue 的修改會立即生效
```

桌面殼會自動尋找空閒連接埠、啟動 API，並把存取權杖注入介面。這一步不需要手動填寫設定。

再次啟動時，程式會聚焦現有視窗，不會另外啟動 API。若本機服務無法啟動或反覆結束，連線頁會顯示失敗原因、結束碼、失敗次數和日誌路徑；查看日誌後即可按「重試」。預設日誌位於 `%LOCALAPPDATA%\Partner\partner-api.log`。

## 3. 設定模型

開啟「設定 → 對話模型」：

1. 選擇介面格式：大多數本機服務使用 OpenAI 相容格式；Claude 或實作 Anthropic Messages 的端點使用 Anthropic。
2. 輸入服務位址：OpenAI 相容服務例如 `http://127.0.0.1:11434/v1`，Anthropic 服務例如 `https://api.anthropic.com/v1`。
3. 從下拉選單選取模型。清單會從端點取得，若取得失敗則顯示原因。
4. 有需要時輸入 API Key。它只寫入不讀回，儲存後介面只顯示已設定。

儲存後設定立即生效。沒有模型時，訊息會停留在佇列中，輸入區會提示尚未設定模型。Anthropic 格式目前不會要求擴充思考；「思考強度」只對 OpenAI 相容端點有效。

儲存設定或確認捨棄修改期間，編輯控制項會暫時鎖定，完成後即可繼續編輯。API Key 儲存後不會回顯。

也可以使用環境變數，設定頁的值優先：

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...       # 選填
export PARTNER_LLM_PROTOCOL=openai   # 選填：openai / anthropic
```

## 4. 開啟語音（選用）

文字輸入不需要語音。若要使用語音，請參閱 [語音功能](./voice.zh-TW.md)：可以下載本機引擎，也可以填入現有的 ASR/TTS 服務位址。

## 5. 資料位置

預設工作區是 `%LocalAppData%/Partner/workspace`：

```
<workspace>/
  session/events.jsonl    工作階段記錄
  queue/items.jsonl       輸入佇列
  memory/memory.db        記憶庫（SQLite）
  memory/backups/         記憶庫備份（每日維護前建立，保留最近 7 份）
  scheduler/              定時任務
  skills/                 技能
  media/                  附件
```

工作階段和佇列都能從記錄重建。記憶庫至少每天備份到 `memory/backups/` 一次：啟動時會補做，每日維護前也會檢查，保留最新 7 份；備份完成後寫入的內容不會出現在該副本裡。

刪除工作階段歷史時，如果記錄檔替換失敗，操作會回報錯誤；只要原檔能重新開啟，仍可繼續寫入。若無法恢復寫入，儲存會進入故障狀態並拒絕後續寫入；請先排除檔案占用或權限問題，再重新啟動應用程式。

## 6. 打包安裝檔

```bash
bash scripts/build-shell.sh      # SKIP_INSTALLER=1 只編譯殼
```

腳本依序執行「發佈 API → 建置前端 → 編譯桌面殼 → 建立 NSIS 安裝包」。順序不能更換，否則安裝檔可能包含舊版 API。

一般安裝流程由 Tauri 的 `beforeBuildCommand` 建置一次前端；使用 `SKIP_INSTALLER=1` 時，腳本會先直接建置一次前端，再編譯桌面殼。直接執行 `tauri build` 仍會建置前端，但必須先自行發佈 API。
