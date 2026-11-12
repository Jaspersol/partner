# 桌面操控

**简体中文** | [繁體中文](./desktop-control.zh-TW.md) | [English](./desktop-control.en.md)

返回 [文档目录](./README.md)。

让模型看窗口、按控件、打字、启动程序。默认关闭；开启后由桌面子 agent 持有这些工具，主 agent 只能委派（见 [子 agent](./sub-agents.md)）。

## 开启

“设置 → 电脑操控”：

- **允许助手操作电脑**：总开关。
- **只允许操作这些窗口**：窗口白名单，留空表示不限。
- **操作时占用屏幕吗**：前台 / 自动 / 后台，见下文“后台输入”。
- 想让模型看截图，在“设置 → 对话模型”打开“能看图片”。纯文本模型不会注册截图工具。

## 工具

| 工具 | 做什么 | 审批 |
|---|---|---|
| `desktop_observe` | 列出窗口和窗口内的可交互控件 | 不需要 |
| `desktop_screenshot` | 截窗口或整个桌面，叠坐标网格和控件编号 | 不需要 |
| `desktop_act` | 聚焦、点击、双击、右键、按键（含组合键）、打字、滚动、拖拽 | 按挡位 |
| `desktop_wait` | 等控件出现、窗口出现或画面静止，最多 5 秒 | 不需要 |
| `desktop_clipboard_get` / `_set` | 读 / 写剪贴板 | 写需要 |
| `desktop_launch` | 启动程序或打开 URI，裸名字先查开始菜单 | 需要 |
| `desktop_exec` | 执行命令并回传输出，30 秒超时 | 需要 |

## 先找控件，再动手

- 观察返回控件和它的点击点，动作用 `elementId` 指定目标，不靠模型猜坐标。
- 控件支持 UI Automation pattern 时直接调用（按钮、列表项、复选框），不需要窗口在前台。
- 动作完成后默认附一张该窗口的新截图（`feedback=auto`；可选 `on-failure`、`off`）。
- Chromium / Electron 应用的页面控件通常直接通过 UI Automation 出现，不需要调试端口；用 `desktop_launch` 启动时会额外开启调试端口，后台输入和页面节点更完整。

## 审批挡位

在会话输入区的下拉里切换，立即生效：

| 挡位 | 桌面动作 |
|---|---|
| 不审批 | 都不问 |
| 危险指令审批 | 只问执行命令、启动程序、写剪贴板，以及系统级快捷键（Win 组合、Alt+F4、Alt+Tab、Ctrl+Alt+*、Ctrl+Shift+Esc） |
| 全部审批（默认） | 每个界面动作都问；观察和截图不问 |

审批提示出现在会话输入框正上方，无人应答即拒绝。在“全部审批”下，批准一次会授权同一窗口继续 10 步（10 分钟内有效）；命令、启动、写剪贴板和系统快捷键仍然逐次询问，系统快捷键也不占用这 10 步租约。

## 护栏

所有护栏都在注入输入之前检查：

- 按键和打字要求目标窗口在前台，否则先聚焦。
- 坐标必须落在目标窗口内、虚拟桌面内；越界直接拒绝，不夹到边缘。
- 落点被别的窗口挡住时，先把目标带到前台再点；带不动就拒绝并说明挡住的是谁。
- 最小化的窗口不能截图、不能点击，需要先聚焦还原。
- 结果里写明目标窗口，以及“已发出”和“画面有没有变化”两件事；第一行是 `{"code","delivery","retry"}`，护栏拒绝是 `not_delivered`+`safe`，已发出但未确认是 `posted_unverified`+`unsafe`。
- 密码字段（UI Automation 的 `IsPassword`，或页面 `type=password` / `autocomplete` 含 password）不输出值；写入后只用“空/已填”比较状态。
- 系统快捷键单独审批：任何含 Win 的组合、Alt+F4、Alt+Tab、Ctrl+Alt+*、Ctrl+Shift+Esc；批准也不会授予任务租约。
- 带 `elementId` 的按键/打字先 SetFocus 并确认 `HasKeyboardFocus`，焦点不在目标控件时拒绝并指出当前焦点。
- 同一窗口里身份相同的控件都标 `ambiguous`，按 `elementId` 操作会返回 `AMBIGUOUS_TARGET`，要求改用坐标或缩小范围。
- 截图前后核对窗口句柄、进程启动时间和矩形；期间移动或换了窗口就丢弃这张图。
- 卡住的窗口先用 `SendMessageTimeout(WM_NULL, 2s)` 预探测，返回 `APP_UNRESPONSIVE`，不进入 UI Automation。

## 后台输入

“操作时占用屏幕吗”（`desktop.inputMode`）：

| 取值 | 行为 |
|---|---|
| `foreground`（默认） | 能走控件 pattern 或页面协议就走，其余抢前台 |
| `auto` | 能在后台完成就在后台，做不到再接管屏幕 |
| `background` | 绝不接管屏幕，做不到就拒绝并给出替代办法 |

后台通道只有两条：页面调试协议（仅限 `desktop_launch` 启动的浏览器 / Electron）和 UI Automation pattern。用户已经开着的程序没有调试端口。

## UIA 客户端

- 使用 UI Automation 的 COM 接口（`CUIAutomation8`），调用固定在专用 MTA 线程；连接超时 2 秒、事务超时 5 秒。等待 Chromium 树稳定在调用方进行，不占用 UIA 队列。
- provider 在另一个进程时用 `CacheRequest`（`AutomationElementMode_Full`）一次取回遍历所需属性；provider 在本进程时不用缓存（本机实测同一 21 节点夹具，缓存 48.4ms vs 27.6ms）。缓存请求不能用默认的 `None`：实测 Edge 页面会只返回 63 个而不是 129 个节点。
- 本机对比：Notepad 43/43、Explorer 7/7、Chromium 窗口 87/87、Edge 页面 127/127 个节点（COM/托管客户端相同）；单窗口 observe p50 为 6.9ms（Explorer）、61.7ms（Chromium，40 控件上限）和 327.0ms（Edge 页面）。
- 台账和复现命令在 `probes/windows/FINDINGS.md`。

## 已知边界

- 调试端口没有认证，监听本机回环地址，本机其他进程也能访问。只在 `desktop_launch` 主动启动时才开。
- 没有做 OCR，UI Automation 看不到的界面交给视觉模型读截图。
- 本机实测：单屏 2560×1440、系统 DPI 96（100%）、虚拟桌面原点 (0,0)。双屏、负坐标副屏和 125%/150% 未实测；`probes/windows/run.ps1 -Command monitors` 可打印屏幕身份与 DPI，验证时用句柄 + 边界 + 主屏标志 + 缩放比例一起比较，不要以 `GetProcessDpiAwareness` 的读回值断言 Per-Monitor V2。
- Chromium 冷启动的“首次读取偏少、随后稳定”未在本机复现；代码仍保留 250ms 重读直到计数稳定的逻辑。
- 真实 Electron 应用（如音乐客户端）的页面控件路径未验证。
