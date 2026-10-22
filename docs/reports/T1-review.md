# T1 主 Agent 复核记录

日期：2026-10-22
结论：**有条件通过**。T1 骨架可运行，三项评审缺陷已修复；主 Agent 复核时又发现并修复了一个宿主完全透明的关键缺陷。T2 可以开始。

## 一、已确认通过

| 项 | 证据 |
|---|---|
| 构建 | `build.bat` 0 error，产物 `bin\VibeBar.exe`（T1 预期的 CS0649 警告） |
| 宿主挂载 | 宿主 HWND 父窗口 = `Shell_TrayWnd`；矩形 `1598,1104,1747,1152`（100% DPI） |
| 位置 | 宿主位于 `TrayNotifyWnd`（左边界 1753）左侧，右边缘留 6–8px |
| 三段文本 | UIAutomation 读到 `额度 —`、`Git —`、`会话 —`，`IsOffscreen=False` |
| 详情面板 | 独立顶层窗口，`GetParent=0`，矩形 `1386,958,1746,1097`，在任务栏上方 7px |
| 面板收起 | 鼠标移开约 363ms 后隐藏 |
| 不抢焦点 | 普通前台窗口与任务栏两种场景下，`GetForegroundWindow` 均未变成宿主/面板 |
| 单实例 | 第二次启动提示已运行，主进程只有一个任务栏子窗口 |
| 设置/凭证/日志 | 35 项临时断言通过；DPAPI 正常；日志对 `sk-` 脱敏 |
| 资源 | 工作集约 74–82MB，空闲 CPU 约 0.07% |

## 二、评审缺陷与修复

### 1. 宿主抢前台（子 Agent 修复）

- 现象：`ShowActivated=false` 对 `SetParent` 后的子窗口不足，启动后宿主成为前台。
- 修复：`Focusable=false`；`WindowInteropHelper.EnsureHandle()` 在 `Show()` 前写入 `WS_EX_NOACTIVATE`，`OnSourceInitialized` 再次确认。
- 复核：宿主 exstyle = `0x08080000`；两种前台场景均未抢前台。

### 2. 多余顶层 Tooltip（子 Agent 修复）

- 现象：悬停时多出一个 30×26 的顶层 WPF Tooltip 窗口。
- 修复：移除 `TaskbarStrip` 中 `TextBlock.ToolTip` 赋值，保留 `SegmentVisual.Tooltip` 契约字段。
- 复核：悬停时进程只有 1 个可见顶层窗口（详情面板）。

### 3. 构建日志乱码（子 Agent 修复）

- 修复：`build.bat` 增加 `chcp 65001` 与 csc `/utf8output`，未改编译参数。

### 4. 宿主完全透明（主 Agent 复核发现并修复）

- 现象：详情面板能显示、鼠标能命中、UIAutomation 能读到三段文字（`IsOffscreen=False`），但屏幕上完全看不到条。
- 原因：`TaskbarHost` 构造时 `Opacity=0`，依赖首次定位后设为 1；`SetParent` 到任务栏后 WPF 的 `Window.Opacity` 变更可能不再生效，整窗以 alpha=0 合成，文字存在但不可见。
- 修复：宿主 `Opacity` 始终为 `1`；非悬停透明完全由 `TaskbarStrip` 背景的 `1/255` alpha 承担。已写入《架构与接口.md》的窗口约束与 HOST-12 用例。
- 复核：重新构建启动后，宿主仍在正确位置，UIAutomation 三段文字可见；最终视觉待用户目视确认。

## 三、仍需人工验收

- 任务栏自动隐藏 / 全屏应用时，条与面板是否一起消失。
- Explorer 重启后宿主自动重建/重新挂载。
- 125% / 150% / 200% DPI。
- 任务栏顶部、多显示器。
- 任务栏宽度不足时的折叠（T5 实现）。
- 托盘菜单「显示/隐藏」「退出」的实际点击。
- 系统深浅色切换。
- 面板与任务栏边缘的视觉裁剪。

## 四、环境说明

当前环境用 GDI `CopyFromScreen`/`PrintWindow` 抓不到 Windows 11 任务栏的合成层与 WPF 分层子窗口（Lyricify Lite 的同类窗口也抓不到），因此截图不能作为「条不可见」的证据；本复核以窗口几何、exstyle、UIAutomation 与用户目视为准。

## 五、下一步

T2：DeepSeek 余额 Provider、峰谷判断、额度段、设置窗口（Key/仓库/刷新）、DPAPI 接入。
