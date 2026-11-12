# Windows 感知探针台账

探针不改产品代码。每条记录：脚本/命令、对象、预期、实测、结论。

运行方式：

```powershell
# 同一进程 vs 另一进程的节点数与缓存耗时
.\run.ps1 -Command local
.\run.ps1 -Command compare

# 某个已打开窗口（标题或进程名的一部分）
.\run.ps1 -Command nodes -Match "文件资源管理器"
.\run.ps1 -Command stabilize -Match "omp web"
.\run.ps1 -Command product -Match "文件资源管理器"

# 屏幕/DPI 布局
.\run.ps1 -Command monitors
```

实测环境：Windows 10.0.26200，x64，.NET 10，单屏 2560×1440，系统 DPI 96（100%）。

Edge 页面用同目录的 `probe-page.html` 打开，配置文件放到临时目录，避免碰用户会话：
`msedge --user-data-dir=<临时目录> --no-first-run --new-window file:///<绝对路径>/probe-page.html`

| 行 | 日期 | 命令 | 对象 | 预期 | 实测 | 结论 |
|---|---|---|---|---|---|---|
| W-01 | 2026-11-12 | `compare` / `nodes` | WinForms 夹具（同进程） | COM 与托管客户端节点数一致 | COM 21，托管 21 | CONFIRMS |
| W-02 | 2026-11-12 | `compare` / `nodes` | WinForms 夹具（另一进程，21 节点） | COM 与托管客户端节点数一致 | COM 21，托管 21 | CONFIRMS |
| W-03 | 2026-11-12 | `nodes "记事本"` | 经典 Notepad（43 节点） | 托管客户端明显少 | COM 43，托管 43 | CONTRADICTS（本机未复现“托管只见 3 个节点”；两客户端看到同一棵 ControlView 树） |
| W-04 | 2026-11-12 | `nodes "文件资源管理器"` | Explorer（7 节点） | 节点数接近 | COM 7，托管 7 | CONFIRMS |
| W-05 | 2026-11-12 | `nodes "omp web"` | Chromium/Electron 窗口（87 节点） | 首次读取偏少，需等待稳定 | 首次即 87，250ms 间隔 12 次均 87 | CONTRADICTS（该窗口已加载完成；冷启动的 13→172 未复现） |
| W-06 | 2026-11-12 | `local` | WinForms 夹具（同进程，21 节点，8 属性/节点） | CacheRequest 同进程更慢 | 无缓存 27.6ms，有缓存 48.4ms（1.75×） | CONFIRMS（按 provider 是否在本进程决定缓存） |
| W-07 | 2026-11-12 | `compare` | WinForms 夹具（另一进程，21 节点） | 小树缓存收益不确定 | 无缓存 28.8ms，有缓存 48.5ms | NEW-EDGE（跨进程但树太小，缓存反而慢；缓存不是无条件的） |
| W-08 | 2026-11-12 | `nodes "omp web"` | Chromium/Electron（87 节点） | 跨进程大树上缓存更快 | 无缓存 97.8ms，有缓存 68.5ms（1.43×） | CONFIRMS |
| W-09 | 2026-11-12 | `nodes "default"` | Chromium/Electron（64 节点） | 跨进程大树上缓存更快 | 无缓存 94.9ms，有缓存 62.5ms（1.52×） | CONFIRMS |
| W-10 | 2026-11-12 | `product "文件资源管理器"` | 产品 observe，单窗口 5 次 | p50 不劣于托管客户端 | p50 6.9ms，min 6.3ms，max 52.1ms，4 个控件 | CONFIRMS |
| W-11 | 2026-11-12 | `product "omp web"` | 产品 observe，单窗口 5 次 | p50 不劣于托管客户端 | p50 61.7ms，min 56.1ms，max 97.6ms，40 个控件（达到每窗口上限） | CONFIRMS |
| W-12 | 2026-11-12 | `monitors` | 本机显示布局 | 记录屏幕身份与 DPI | 虚拟桌面 (0,0) 2560×1440；1 块 DISPLAY10 主屏；96 DPI | CONFIRMS（本机为单屏 100%） |
| W-13 | 2026-11-12 | `monitors` | 双屏/125%/150% | 归一化与截图对齐 | 本机无此硬件，未测 | 待补（方法已写入 `doc/desktop-control*.md` 的“已知边界”） |

| W-14 | 2026-11-12 | `nodes "UIA Probe Page"` | Edge 打开本地页（标题 UIA Probe Page） | 首次读取只有外壳，随后稳定 | 129 节点，t=0 起 12 次读数均 129 | CONTRADICTS（本机已加载页面未复现外壳阶段；等待逻辑保留） |
| W-15 | 2026-11-12 | `nodes "UIA Probe Page"` | Edge 页面（127–129 节点） | CacheRequest 不应改变看到的节点数 | 默认 `AutomationElementMode_None` 时 cached 63 vs uncached 129；改为 `Full` 后 127 vs 127 | NEW-EDGE（缓存模式必须是 Full，否则缓存树遍历会漏节点） |
| W-16 | 2026-11-12 | `product "UIA Probe Page"` | 产品 observe Edge 页面，5 次 | 不开调试端口也能列出页面控件 | p50 327.0ms，min 320.1ms，max 366.5ms，40 个控件（达到每窗口上限） | CONFIRMS |

## 结论摘要

- 节点数差异未在本机复现：COM 与托管客户端在 Notepad、Explorer、Chromium 窗口和 WinForms 夹具上返回相同节点数（W-01…W-05）。迁移到 COM 的理由由超时、MTA 和缓存收益支撑，而不是“托管看不见”。
- 缓存策略应按 provider 是否在客户端进程内决定（W-06）；跨进程大树收益明显（W-08、W-09），但小树可能倒退（W-07），因此不按节点数做全局开关。
- Chromium 首次读取不稳定在本机未复现（W-05）；产品仍保留 250ms 重读直到计数稳定的逻辑，冷启动窗口需要另行测。
- 产品 observe 的单窗口 p50 为 6.9ms（Explorer）和 61.7ms（Chromium，40 控件上限）（W-10、W-11）；Edge 本地页为 327.0ms（W-16）。
- CacheRequest 必须显式设置 `AutomationElementMode_Full`；默认的 `None` 会让缓存树遍历只返回约一半节点（63 vs 129，W-15）。
- Edge 已加载页面首次读取即稳定在 129 节点（W-14）；Chromium 冷启动的外壳阶段仍需在真实冷启动上补测。
