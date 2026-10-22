# T4 主 Agent 复核记录

日期：2026-10-22
结论：**通过（含一次真实安装缺陷修复）**。Hooks 事件桥、会话段、安装/卸载已接入真实 Claude Code；当前 Hooks 已安装，等待用户重启 Claude Code 会话后生效。

## 一、合成事件验收

| 项 | 结果 |
|---|---|
| SessionStart | 段显示 `空闲` |
| UserPromptSubmit | `思考中` |
| PreToolUse(Bash) | `Bash` |
| PermissionRequest | `等待确认`（代码设置蓝色呼吸，UIAutomation 验证文本） |
| Stop | `完成` |
| 多会话 | `2 会话`；任一等待确认时 `2 会话 · 等待确认` |
| SessionEnd | 逐步减少，最后 `会话 —` |
| 面板 | `Claude 会话 / 任务栏工具 · <状态> / 最后事件：...` |
| 程序未运行 CLI | 退出码 0，无 stdout/stderr；热启动约 39–49ms |
| 非法输入 | 坏 JSON、v=2、100KB、半条消息断开后仍可继续处理有效事件 |

## 二、真实安装与卸载

- 手动备份：`%USERPROFILE%\.claude\settings.json.vibebar-manual-backup-20261022`（安装前原文）。
- 安装：10 个事件各 1 个 VibeBar 条目；原有 `SessionStart` / `Notification` / `UserPromptSubmit` 插件条目、`env`、`model`、`language`、`statusLine` 全部保留（与备份深度比较一致）。
- 卸载：VibeBar 条目 0；`hooks` 与安装前备份深度一致；插件条目仍为 1。
- 当前状态：已重新安装（10 条），备份仍在。

## 三、真实 Claude Code 验证

- 首次安装后运行 `claude -p "只回复 OK"`，出现：
  `SessionEnd hook [...] failed: Executable not found in $PATH: ""E:\Acode\任务栏工具\bin\VibeBar.Cli.exe""`
- 根因：`HookInstaller` 给 `command` 加了引号；Claude Code 把 `command` 当作可执行文件名解析，不经过 shell。
- 修复：`command` 改为**无引号绝对路径**（当前路径无空格）；重新安装。
- 再次运行 `claude -p "只回复 OK"`：输出无 hook 报错；任务栏出现 `任务栏工具 · Bash`，面板显示最后事件时间，证明 SessionStart/UserPromptSubmit/PreToolUse 事件已贯通。
- 注意：`claude -p` 下本次未观察到 `Stop`/`SessionEnd` 事件到达（会话停留在 `Bash`）；交互式会话的 `Stop`/`PermissionRequest` 仍需用户实际使用确认。状态机本身已由合成事件验证。

## 四、安全与环境说明

- 未在任何日志中输出 Token；安装器只重写 `hooks`，不改 `env`/`statusLine`。
- .NET Framework 4.8 无 `PipeOptions.CurrentUserOnly`，使用仅授予当前用户的 `PipeSecurity` ACL，语义等价。
- CLI 冷启动首次约 118ms，热启动 39–49ms；Hook 超时 5s，不会阻塞会话。
- 真实 `settings.json` 已被安装器重新格式化（JSON 序列化），数据与备份一致；如不希望保留，可用备份恢复。

## 五、仍需人工/后续验收

- 交互式 Claude Code：权限请求时任务栏「等待确认」呼吸；会话结束/Stop 后「完成」30s 回空闲。
- 设置窗口「安装/卸载 Hooks」按钮与确认框实际点击。
- Explorer 重启后 EventServer/宿主恢复。
- 长时间多会话、子代理事件。
- statusLine 上下文/模型（T5，可选）。

## 六、下一步

T5：详情面板动作与排版、隐私热键、开机自启、宽度折叠、主题打磨、README；可选 statusLine。
