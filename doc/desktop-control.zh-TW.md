# 桌面操控

[简体中文](./desktop-control.md) | **繁體中文** | [English](./desktop-control.en.md)

返回 [文件目錄](./README.zh-TW.md)。

讓模型看視窗、按控制項、打字、啟動程式。預設關閉；開啟後由桌面子 agent 持有這些工具，主 agent 只能委派（見 [子 agent](./sub-agents.zh-TW.md)）。

## 開啟

「設定 → 電腦操控」：

- **允許助手操作電腦**：總開關。
- **只允許操作這些視窗**：視窗白名單，留空表示不限。
- **操作時佔用螢幕嗎**：前景 / 自動 / 背景，見下文「背景輸入」。
- 想讓模型看截圖，在「設定 → 對話模型」開啟「能看圖片」。純文字模型不會註冊截圖工具。

## 工具

| 工具 | 做什麼 | 審批 |
|---|---|---|
| `desktop_observe` | 列出視窗和視窗內可互動的控制項 | 不需要 |
| `desktop_screenshot` | 截取視窗或整個桌面，疊上座標格線和控制項編號 | 不需要 |
| `desktop_act` | 聚焦、點擊、雙擊、右鍵、按鍵（含組合鍵）、打字、捲動、拖曳 | 依檔位 |
| `desktop_wait` | 等控制項出現、視窗出現或畫面靜止，最多 5 秒 | 不需要 |
| `desktop_clipboard_get` / `_set` | 讀 / 寫剪貼簿 | 寫需要 |
| `desktop_launch` | 啟動程式或開啟 URI，單純名稱先查開始功能表 | 需要 |
| `desktop_exec` | 執行指令並回傳輸出，30 秒逾時 | 需要 |

## 先找控制項，再動手

- 觀察回傳控制項和它的點擊點，動作用 `elementId` 指定目標，不靠模型猜座標。
- 控制項支援 UI Automation pattern 時直接呼叫（按鈕、清單項目、核取方塊），不需要視窗在前景。
- 動作完成後預設附一張該視窗的新截圖（`feedback=auto`；可選 `on-failure`、`off`）。
- Chromium / Electron 應用的頁面控制項通常直接透過 UI Automation 出現，不需要偵錯連接埠；用 `desktop_launch` 啟動時會額外開啟偵錯連接埠，背景輸入和頁面節點更完整。

## 審批檔位

在工作階段輸入區的下拉選單切換，立即生效：

| 檔位 | 桌面動作 |
|---|---|
| 不審批 | 都不問 |
| 危險指令審批 | 只問執行指令、啟動程式、寫剪貼簿，以及系統級快速鍵（Win 組合、Alt+F4、Alt+Tab、Ctrl+Alt+*、Ctrl+Shift+Esc） |
| 全部審批（預設） | 每個介面動作都問；觀察和截圖不問 |

審批提示出現在工作階段輸入框正上方，無人回應即拒絕。在「全部審批」下，批准一次會授權同一視窗繼續 10 步（10 分鐘內有效）；指令、啟動、寫剪貼簿和系統快速鍵仍然逐次詢問，系統快速鍵也不佔用這 10 步租約。

## 護欄

所有護欄都在注入輸入之前檢查：

- 按鍵和打字要求目標視窗在前景，否則先聚焦。
- 座標必須落在目標視窗內、虛擬桌面內；越界直接拒絕，不夾到邊緣。
- 落點被別的視窗擋住時，先把目標帶到前景再點；帶不動就拒絕並說明擋住的是誰。
- 最小化的視窗不能截圖、不能點擊，需要先聚焦還原。
- 結果裡寫明目標視窗，以及「已發出」和「畫面有沒有變化」兩件事；第一行是 `{"code","delivery","retry"}`，護欄拒絕是 `not_delivered`+`safe`，已發出但未確認是 `posted_unverified`+`unsafe`。
- 密碼欄位（UI Automation 的 `IsPassword`，或頁面 `type=password` / `autocomplete` 含 password）不輸出值；寫入後只用「空/已填」比較狀態。
- 系統快速鍵單獨審批：任何含 Win 的組合、Alt+F4、Alt+Tab、Ctrl+Alt+*、Ctrl+Shift+Esc；批准也不會授予任務租約。
- 帶 `elementId` 的按鍵/打字先 SetFocus 並確認 `HasKeyboardFocus`，焦點不在目標控制項時拒絕並指出目前焦點。
- 同一視窗裡身分相同的控制項都標 `ambiguous`，按 `elementId` 操作會回傳 `AMBIGUOUS_TARGET`，要求改用座標或縮小範圍。
- 截圖前後核對視窗代碼、行程啟動時間和矩形；期間移動或換了視窗就丟棄這張圖。
- 卡住的視窗先用 `SendMessageTimeout(WM_NULL, 2s)` 預探測，回傳 `APP_UNRESPONSIVE`，不進入 UI Automation。

## 背景輸入

「操作時佔用螢幕嗎」（`desktop.inputMode`）：

| 值 | 行為 |
|---|---|
| `foreground`（預設） | 能走控制項 pattern 或頁面協定就走，其餘搶前景 |
| `auto` | 能在背景完成就在背景，做不到再接管螢幕 |
| `background` | 絕不接管螢幕，做不到就拒絕並給出替代辦法 |

背景通道只有兩條：頁面偵錯協定（僅限 `desktop_launch` 啟動的瀏覽器 / Electron）和 UI Automation pattern。使用者已經開著的程式沒有偵錯連接埠。

## UIA 用戶端

- 使用 UI Automation 的 COM 介面（`CUIAutomation8`），呼叫固定在專用 MTA 執行緒；連線逾時 2 秒、交易逾時 5 秒。等待 Chromium 樹穩定在呼叫方進行，不佔用 UIA 佇列。
- provider 在另一個行程時用 `CacheRequest`（`AutomationElementMode_Full`）一次取回遍歷所需屬性；provider 在本行程時不用快取（本機實測同一 21 節點夾具，快取 48.4ms vs 27.6ms）。快取請求不能用預設的 `None`：實測 Edge 頁面會只回傳 63 個而不是 129 個節點。
- 本機對比：Notepad 43/43、Explorer 7/7、Chromium 視窗 87/87、Edge 頁面 127/127 個節點（COM/受控用戶端相同）；單視窗 observe p50 為 6.9ms（Explorer）、61.7ms（Chromium，40 控制項上限）和 327.0ms（Edge 頁面）。
- 台賬和重現命令在 `probes/windows/FINDINGS.md`。

## 已知邊界

- 偵錯連接埠沒有驗證，監聽本機迴路位址，本機其他行程也能存取。只在 `desktop_launch` 主動啟動時才開。
- 沒有做 OCR，UI Automation 看不到的介面交給視覺模型讀截圖。
- 本機實測：單螢幕 2560×1440、系統 DPI 96（100%）、虛擬桌面原點 (0,0)。雙螢幕、負座標副螢幕和 125%/150% 未實測；`probes/windows/run.ps1 -Command monitors` 可列印螢幕身分與 DPI，驗證時用代碼 + 邊界 + 主螢幕旗標 + 縮放比例一起比較，不要以 `GetProcessDpiAwareness` 的讀回值斷言 Per-Monitor V2。
- Chromium 冷啟動的「首次讀取偏少、隨後穩定」未在本機重現；程式仍保留 250ms 重讀直到計數穩定的邏輯。
- 真實 Electron 應用程式（如音樂用戶端）的頁面控制項路徑未驗證。
