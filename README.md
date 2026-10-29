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
| 模型与上下文 | 优先用 statusLine 官方数据；缺失时从会话转录的 `message.usage` 兜底估算；空闲时任务栏段显示 `34%` |
| 本地服务（端口） | 枚举 LISTEN 端口 → PID → 进程名；段显示 `:5173` 或 `:3000 :5173 +1`，面板可按端口打开 `http://localhost:端口`；无监听端口时自动隐藏 |
| PR / CI | 通过 `gh pr view` 显示 `#123 ✓` / `#123 ✗2` / `#123 …1`；无 PR、未装 gh 时自动隐藏，面板可打开 PR |
| 自定义命令 | 一条命令一个段，`cmd` 执行 + 3 秒超时；支持纯文本首行或 JSON `{text,tooltip,color,url}` 协议 |
| 详情面板 | 额度、Git、会话分区；无数据区块自动隐藏；内容过长可滚动；按钮操作不抢焦点；右上角「×」可直接关闭，固定后鼠标离开 4 秒自动收起 |
| 宽度折叠 | 任务栏空间不足时按 Git → 非等待会话 → 额度的顺序折叠为 `+N`，面板仍显示全部 |
| 显示区域 | 设置中可选靠左/靠右吸附，再调水平偏移与最大宽度，以及额度/Git/会话三个模块的显隐 |
| 显示器 | 设置中可选主屏或副屏任务栏，面板与设置窗口都跟随悬浮条所在显示器；DPI 缩放、显示设置变更、竖向任务栏都会自动重排 |
| 悬停背景 | 设置中可选跟随主题色，或自定义 `#RRGGBB` + 0–100% 透明度，带实时预览 |
| 主题跟随 | 任务栏条/详情面板跟随「系统」深浅色，设置窗口跟随「应用」深浅色；输入框、下拉框、复选框均为代码构建模板，深色下不会出现白底浅字 |
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
├─ statusline-original.txt# 用户原有 statusLine 命令（安装时自动备份，卸载恢复）
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
  "ports": {
    "pollSeconds": 5,
    "watchPorts": [3000, 3001, 4200, 4321, 5000, 5173, 8000, 8080, 9000]
  },
  "pr": {
    "pollSeconds": 60
  },
  "custom": {
    "commands": [
      {
        "id": "build",
        "label": "构建",
        "command": "npm run build --silent",
        "intervalSeconds": 60,
        "format": "text",
        "url": "",
        "enabled": true
      }
    ]
  },
  "ui": {
    "maxWidth": 420,
    "minWidth": 120,
    "anchor": "right",
    "offsetX": 8,
    "monitor": "primary",
    "segments": [
      { "id": "quota", "enabled": true, "priority": 10 },
      { "id": "git", "enabled": true, "priority": 20 },
      { "id": "agent", "enabled": true, "priority": 30 },
      { "id": "ports", "enabled": false, "priority": 40 },
      { "id": "pr", "enabled": false, "priority": 50 },
      { "id": "build", "enabled": true, "priority": 60 }
    ],
    "hoverColor": "",
    "hoverOpacity": 235
  }
}
```

- 未知键在保存时会保留。
- `quota.refreshSeconds` 最低 60 秒；`git.pollSeconds` 最低 10 秒。
- `ui.anchor` 为 `right`（靠右吸附，偏移从托盘左缘算）或 `left`（靠左吸附，偏移从任务栏左缘算）；`ui.offsetX` 为 0–400 px；`ui.hoverColor` 为空表示跟随主题色，格式 `#RRGGBB`；`ui.hoverOpacity` 为 0–255（0 表示悬停也完全透明）。
- `ui.segments` 控制各信息段的显隐与排序；新增段时注册 Presenter 后会由程序自动补齐默认项，旧版 `showQuota/showGit/showAgent` 会自动迁移。
- `ui.monitor` 为 `primary`（主屏任务栏）或 `secondary`（副屏 `Shell_SecondaryTrayWnd`，不存在时回退主屏）。
- `ports.watchPorts` 为空数组表示显示全部监听端口；非空时只显示列表内的端口（1–65535，自动去重）。`ports.pollSeconds` 范围 2–600。
- `pr.pollSeconds` 范围 30–3600；PR 段复用 `git.repoPath`，需要本机安装并登录 GitHub CLI（`gh auth login`）。
- `custom.commands` 每条最多 8 条：`command` 为空会被忽略，`id` 缺失/重复自动补齐，`intervalSeconds` 收敛到 10–3600，`format` 仅 `text`/`json`。命令以 `cmd /d /c` 在本机执行，超时 3 秒。
- API Key 有两种来源：
  1. 在设置窗口的「API Key」中填写，保存到 `secrets.dat`（DPAPI 加密）；
  2. 勾选「从 Claude Code 配置读取 Key」，运行时从 `%USERPROFILE%\.claude\settings.json` 的 `env.ANTHROPIC_AUTH_TOKEN` 读取，只在内存中使用。
- Key 不会写入 `settings.json`，也不会出现在日志或面板中。

## Claude Code Hooks 与上下文

1. 托盘 →「设置」→「安装 Hooks + statusLine」。
2. 安装前会在 `%USERPROFILE%\.claude\settings.json` 同目录生成
   `settings.json.vibebar.<yyyyMMddHHmmss>.bak` 备份。
3. 安装器只添加/移除包含 `VibeBar.Cli.exe` 的条目，保留其他插件与配置；
   statusLine 用于显示模型与上下文占用，用户原有的 statusLine 命令会透传并在卸载时恢复。
4. Hooks/statusLine 对新启动的 Claude Code 会话生效；已运行的会话需要重启。
5. `claude -p` 打印模式可能不触发 statusLine；交互式 TUI 会话效果最完整。
5. 若移动了 VibeBar 程序目录，请在设置中重新执行「安装 Claude Code Hooks」，
   因为 Hooks 中记录的是 CLI 的绝对路径。
6. 卸载：设置 →「卸载 Hooks」。只移除 VibeBar 条目，不影响其他 Hooks。

> v1 不安装/透传 Claude Code `statusLine`；`statusline-original.txt` 为后续版本预留。

## 扩展段：端口 / PR / 自定义命令

这三个段默认关闭（自定义命令除外，配置即生效），在设置 →「显示模块」里勾选后显示。

### 本地服务（端口）

- 用 IP Helper API（`GetExtendedTcpTable`）枚举 LISTEN 状态的 TCP 端口，再取 PID 与进程名，不解析 `netstat` 文本（避免中文系统输出差异）。
- 段显示前两个端口，其余折叠为 `+N`；没有匹配端口时整段隐藏，详情面板也不占位。
- 面板按钮：`打开 :5173`（默认浏览器打开 `http://localhost:端口`）、`复制端口`。

### PR / CI

- 在 `git.repoPath` 目录执行 `gh pr view --json number,title,state,isDraft,reviewDecision,statusCheckRollup,url`。
- 段显示 `#123 ✓`（全部通过）/ `#123 ✗2`（2 个失败）/ `#123 …1`（1 个等待）；草稿追加 `草稿`。
- 没有打开的 PR、未安装 `gh`、未登录时只隐藏该段，不影响其他段；面板会说明原因。

### 自定义命令

命令以 `cmd /d /c <命令>` 执行，超时 3 秒，输出支持两种协议：

1. **纯文本**（`format: "text"`）：取首个非空行，超过 48 字截断；
2. **JSON**（`format: "json"`）：

```json
{"text":"构建通过","tooltip":"耗时 12s","color":"success","url":"https://ci.example/1"}
```

`color` 取 `neutral|accent|warning|critical|waiting|success`。JSON 解析失败会自动降级为纯文本；
退出码非 0 时仍显示输出，颜色降级为警告，详情里给出退出码与耗时。

> ⚠ 自定义命令由你本人编写并在本机执行，效果等同于在命令行直接运行，请只填写可信命令。

## 隐私模式与开机自启

- 隐私模式：托盘右键 →「隐私模式」。切换后立即重绘任务栏段与详情面板，并写入
  `settings.json`，重启后保持。
- 开机自启：托盘右键 →「开机自启」。勾选状态读取自注册表实际值；切换后写入
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `VibeBar` 值（带引号的
  可执行文件绝对路径），并同步保存到 `settings.json`。

## 已知限制

- 任务栏位置、多显示器、不同 DPI 缩放（125%/150%/200%）仍需在更多环境验证；当前已支持主/副屏任务栏选择、DPI 变更与竖向任务栏重排，但极端分辨率组合未做穷举。
- `claude -p` 打印模式可能不触发 statusLine；此时上下文会在下一次 Hook 事件时从会话转录兜底读取，交互式会话最完整。
- 不合并旧的外设电量监控工具。
- 余额来自 DeepSeek 官方接口；日志、余额等数据只在本地处理，不上传。
- Git 监听针对当前工作区，超大仓库/网络盘可能需要更长刷新时间。
- Hook 安装目前只支持 Claude Code 的 `settings.json`。
- 端口段只能看到 TCP 监听端口，不含 UDP；进程名对系统进程可能为空（权限限制）。
- PR 段依赖 GitHub CLI 且需要登录；私有仓库/企业实例以 `gh` 自身配置为准。
- 自定义命令以当前用户权限执行，未做沙箱；超时只杀直接子进程。
- 详情面板固定（点击段）后，鼠标离开面板与悬浮条 4 秒会自动收起，避免面板一直留在屏幕上。
- 副屏模式取「第一个可见的 `Shell_SecondaryTrayWnd`」，多副屏时不支持指定第 N 块屏。
- 面板与设置窗口的 DPI 换算是按窗口当前 `VisualTreeHelper.GetDpi` 计算，混合 DPI 下可能有一两像素偏差。

## 项目结构

```
src/
├─ App/        入口、AppController、设置、凭证、日志、Win32
├─ Models/     数据契约与段视图模型
├─ Ui/         任务栏宿主、段布局、详情面板、托盘、设置窗口
├─ Providers/  DeepSeek / Git / Claude 会话 / 端口 / PR / 自定义命令
├─ Segments/   段契约、注册表、峰谷、Presenters/
├─ EventBus/   Hooks 命名管道与安装器
└─ Cli/        VibeBar.Cli.exe

tests/         离线自测（纯逻辑，不依赖 WPF）
```

新增信息显示的标准步骤（Provider → Presenter → 注册 → 设置自动出现开关）见 [CLAUDE.md](CLAUDE.md)。

## 自测

```bat
tests\build-tests.bat
```

只编译 Provider / SegmentFactory / 设置收敛等纯逻辑代码，不启动界面、不读写 `%APPDATA%\VibeBar\settings.json`。
当前覆盖：设置收敛、端口段格式化与过滤、IP Helper 枚举、PR JSON 解析、自定义命令文本/JSON 协议、`cmd` 实际执行与 3 秒超时。

## 开发文档

- [项目上下文 CLAUDE.md](CLAUDE.md)
- [功能设计](功能设计.md)
- [架构与接口](架构与接口.md)
- [验收与测试](验收与测试.md)
