# 快速上手

[简体中文](./getting-started.md) | **繁體中文** | [English](./getting-started.en.md)

返回 [文件目錄](./README.zh-TW.md)。

## 1. 準備環境

| 項目 | 要求 |
|---|---|
| 系統 | Windows 10 2004 以上 |
| .NET | .NET 8 SDK |
| Node | Node 22 + npm。不要用 pnpm：它預設攔截相依套件的建置腳本，esbuild 裝不上 |
| Rust | cargo 1.98+，只有桌面殼需要 |
| WebView2 | Win10+ 內建 |
| 硬體 | 語音需要麥克風和喇叭；本機 TTS 需要顯示卡 |

## 2. 啟動

```bash
npm --prefix ui install          # 第一次
npx --prefix ui tauri dev        # 開發模式，改 Vue 立即生效
```

桌面殼會自己挑空閒連接埠、帶起 API、把存取權杖注入介面。這一步不需要填任何設定。

## 3. 設定模型

開啟「設定 → 對話模型」：

1. 填 OpenAI 相容的服務位址，例如 `http://127.0.0.1:11434/v1`。
2. 從下拉選單選模型。清單是從端點拉取的，拉不到會寫明原因。
3. 需要時填 API Key。它只能寫不能讀，儲存後介面只顯示「已設定」。

儲存後立即生效。沒設定模型時，訊息會停在佇列裡，輸入區寫明「未設定模型」。

也可以用環境變數，設定頁的值優先：

```bash
export PARTNER_LLM_BASE_URL=http://127.0.0.1:11434/v1
export PARTNER_LLM_MODEL=qwen3
export PARTNER_LLM_API_KEY=...   # 選填
```

## 4. 開啟語音（選用）

文字輸入不依賴語音。要用語音，見 [語音功能](./voice.zh-TW.md)：可以下載本機引擎，也可以填已有的 ASR/TTS 服務位址。

## 5. 資料位置

預設工作區是 `%LocalAppData%/Partner/workspace`：

```
<workspace>/
  session/events.jsonl    工作階段記錄
  queue/items.jsonl       輸入佇列
  memory/memory.db        記憶庫（SQLite）
  scheduler/              定時任務
  skills/                 技能
  media/                  附件
```

工作階段和佇列可以從記錄重建。記憶庫是唯一儲存，刪了就沒了。

## 6. 打包安裝檔

```bash
bash scripts/build-shell.sh      # SKIP_INSTALLER=1 只編譯殼
```

腳本依「發佈 API → 建置前端 → 編譯殼 → 打包 NSIS」的順序執行。順序不能換，否則安裝檔裡會是舊的 API。
