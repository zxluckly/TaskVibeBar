# VibeBar

VibeBar 是一个驻留在 Windows 任务栏通知区左侧的轻量状态条，用最短的文字展示 DeepSeek 余额与峰谷时段、Git 仓库状态和 Claude Code 会话状态。鼠标悬停可查看详情，点击段落可固定详情面板。

- 纯 .NET Framework 4.8 + WPF，代码构建界面，无 NuGet、无 XAML。
- 两个进程：`VibeBar.exe`（界面/托盘）与 `VibeBar.Cli.exe`（Hook 事件桥）。
- 默认不打扰：空闲时几乎透明，只在悬停或需要确认时突出显示。

## 功能

| 模块 | 说明 |
|---|---|
| DeepSeek 额度 | 显示 `¥23.40 峰` / `¥23.40 谷`；面板显示余额、赠送/充值、下次峰谷切换倒计时 |
| 峰谷判断 | 默认工作日 09:00–12:00、14:00–18:00 为峰时，周末全天谷时；可在 `settings.json` 覆盖 |
| Git | 显示 `分支*脏数`、`↑领先↓落后`、detached、冲突；文件变化约 2 秒内更新 |
| Claude 会话 | 通过 Hooks 显示空闲、思考中、工具、等待确认、完成；等待确认时呼吸提示 |
| 详情面板 | 额度、Git、会话分区；无数据区块自动隐藏；内容过长可滚动；按钮操作不抢焦点 |
| 宽度折叠 | 任务栏空间不足时按 Git → 非等待会话 → 额度的顺序折叠为 `+N`，面板仍显示全部 |
| 显示区域 | 设置中可调距托盘偏移、最大宽度，以及额度/Git/会话三个模块的显隐 |
| 悬停背景 | 设置中可选跟随主题色，或自定义 `#RRGGBB` + 0–100% 透明度，带实时预览 |
| 隐私模式 | 金额显示为 `¥•••`，仓库名/分支/项目名显示为 `…`；托盘一键切换并持久化 |
| 开机自启 | 写入当前用户 `HKCU\...\Run\VibeBar`，托盘勾选状态与注册表一致 |
| 托盘菜单 | 显示/隐藏、刷新全部、隐私模式、开机自启、设置、打开数据目录、退出 |

## 系统要求

- Windows 10 / Windows 11（64 位 Explorer）。
- .NET Framework 4.8（Windows 10 1903 及以后通常已自带）。
- 不需要安装 .NET SDK、Visual Studio 或 MSBuild。

## 构建

在项目根目录运行：

```bat
build.bat
```

脚本使用系统自带的 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 编译，产物：

```text
bin\VibeBar.exe
bin\VibeBar.Cli.exe
```

`VibeBar.Cli.exe` 仅供 Claude Code Hooks 调用，不依赖 WPF，也不需要手动运行。

## 运行

双击 `bin\VibeBar.exe`。程序为单实例；再次启动只提示“已在运行”。

- 首次启动若没有 API Key，会自动打开设置窗口。
- 悬停任务栏状态条约 150ms 后显示详情面板，移开 300ms 后收起。
- 点击某个段可固定面板；再次点击同一段取消固定。固定状态下移开鼠标不会收起。

## 配置与数据

所有本地数据位于：

```text
%APPDATA%\VibeBar\
├─ settings.json          # 普通配置（不含 API Key）
├─ secrets.dat            # DPAPI 加密的 API Key（当前 Windows 用户）
├─ statusline-original.txt# 预留（v1 未实现 statusLine 透传）
└─ log.txt                # 运行日志（自动轮转，日志脱敏）
```

打开数据目录：托盘右键 →「打开数据目录」。

`settings.json` 主要字段：

```json
{
  "general": {
    "showTaskbar": true,
    "theme": "system",
    "autoStart": false,
    "privacyMode": false
  },
  "quota": {
    "baseUrl": "https://api.deepseek.com",
    "readKeyFromClaudeSettings": false,
    "refreshSeconds": 60,
    "warnBelow": 20,
    "criticalBelow": 5
  },
  "git": {
    "repoPath": "",
    "includeUntracked": true,
    "pollSeconds": 30
  },
  "agent": {
    "enabled": true,
    "staleMinutes": 10
  },
  "ui": {
    "maxWidth": 420,
    "minWidth": 120,
    "offsetX": 8,
    "showQuota": true,
    "showGit": true,
    "showAgent": true,
    "hoverColor": "",
    "hoverOpacity": 235
  }
}
```

- 未知键在保存时会保留。
- `quota.refreshSeconds` 最低 60 秒；`git.pollSeconds` 最低 10 秒。
- `ui.offsetX` 为距托盘左缘的偏移（px，0–400）；`ui.hoverColor` 为空表示跟随主题色，格式 `#RRGGBB`；`ui.hoverOpacity` 为 0–255（0 表示悬停也完全透明）。
- API Key 有两种来源：
  1. 在设置窗口的「API Key」中填写，保存到 `secrets.dat`（DPAPI 加密）；
  2. 勾选「从 Claude Code 配置读取 Key」，运行时从 `%USERPROFILE%\.claude\settings.json` 的 `env.ANTHROPIC_AUTH_TOKEN` 读取，只在内存中使用。
- Key 不会写入 `settings.json`，也不会出现在日志或面板中。

## Claude Code Hooks

1. 托盘 →「设置」→「安装 Claude Code Hooks」。
2. 安装前会在 `%USERPROFILE%\.claude\settings.json` 同目录生成
   `settings.json.vibebar.<yyyyMMddHHmmss>.bak` 备份。
3. 安装器只添加/移除包含 `VibeBar.Cli.exe` 的条目，保留其他插件与配置。
4. Hooks 对新启动的 Claude Code 会话生效；已运行的会话需要重启。
5. 若移动了 VibeBar 程序目录，请在设置中重新执行「安装 Claude Code Hooks」，
   因为 Hooks 中记录的是 CLI 的绝对路径。
6. 卸载：设置 →「卸载 Hooks」。只移除 VibeBar 条目，不影响其他 Hooks。

> v1 不安装/透传 Claude Code `statusLine`；`statusline-original.txt` 为后续版本预留。

## 隐私模式与开机自启

- 隐私模式：托盘右键 →「隐私模式」。切换后立即重绘任务栏段与详情面板，并写入
  `settings.json`，重启后保持。
- 开机自启：托盘右键 →「开机自启」。勾选状态读取自注册表实际值；切换后写入
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `VibeBar` 值（带引号的
  可执行文件绝对路径），并同步保存到 `settings.json`。

## 已知限制

- 任务栏位置、多显示器、不同 DPI 缩放（125%/150%/200%）仍需在更多环境验证。
- Claude Code `statusLine`（模型/上下文占用）在 v1 中未实现。
- 不合并旧的外设电量监控工具。
- 余额来自 DeepSeek 官方接口；日志、余额等数据只在本地处理，不上传。
- Git 监听针对当前工作区，超大仓库/网络盘可能需要更长刷新时间。
- Hook 安装目前只支持 Claude Code 的 `settings.json`。

## 开发文档

- [功能设计](功能设计.md)
- [架构与接口](架构与接口.md)
- [验收与测试](验收与测试.md)
