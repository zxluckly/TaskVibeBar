# 外部段接入示例

前提：先在项目根目录运行 `build.bat`，并让 `bin\VibeBar.exe` 保持运行（`settings.json` 里 `external.enabled` 默认是 `true`）。

三个场景都可以直接复制到 PowerShell 里运行。协议细节见 [`docs/插件协议.md`](../docs/插件协议.md)。

---

## 场景一：PowerShell 一行推送

最短的用法，不写任何脚本，直接调 CLI：

```powershell
.\bin\VibeBar.Cli.exe --segment id=demo.ps text="来自脚本" label=演示 color=accent ttl=60
```

- 成功时**完全静默**，`$LASTEXITCODE` 为 0；任务栏出现 `演示 来自脚本`。
- 想看服务端回复就加 `--verbose`：`.\bin\VibeBar.Cli.exe --segment id=demo.ps text=hi --verbose` → 打印 `{"ok":true}`。
- 带明细行（`|` 分隔，最多 8 行）：

```powershell
.\bin\VibeBar.Cli.exe --segment id=demo.ps text="3 项待办" detail="写文档|跑测试|发版" ttl=120
```

- 删除单个段 / 清空某个来源的全部段：

```powershell
.\bin\VibeBar.Cli.exe --segment-remove id=demo.ps
.\bin\VibeBar.Cli.exe --segment-clear id=demo
```

也可以用参数化包装脚本（退出码原样透传，适合放进自己的流水线）：

```powershell
.\examples\push-segment.ps1 -Id demo.build -Text "通过 12s" -Label 构建 -Color success -Ttl 60
```

### 退出码

| 码 | 含义 | 处理建议 |
|---|---|---|
| 0 | 成功 | 无需处理 |
| 2 | 参数错误 | 看 stderr 里的 `missing_id` / `missing_text` / `unknown_key:xxx` |
| 3 | VibeBar 未运行或未启用外部段 | 启动 `bin\VibeBar.exe`，或检查 `settings.json` 的 `external.enabled` |
| 4 | 服务端拒绝 | stderr 是协议错误码，如 `invalid_id`、`too_many_segments`、`rate_limited` |

---

## 场景二：Napcat（QQ）未读数

`examples\napcat-qq.ps1` 接收 OneBot v11 事件 JSON，把未读数推到 `napcat.qq` 段。

事件 JSON 从参数传入：

```powershell
.\examples\napcat-qq.ps1 -EventJson '{"post_type":"message","message_type":"group","group_id":123,"sender":{"card":"小明"},"raw_message":"在吗"}'
```

或从管道传入（适合把 Napcat 的 HTTP 上报直接转发过来）：

```powershell
'{"post_type":"message","message_type":"private","sender":{"nickname":"小红"},"raw_message":"晚点聊"}' | .\examples\napcat-qq.ps1
```

上报端自己聚合了未读数时，直接在事件里带 `unread` 字段最省事：

```powershell
.\examples\napcat-qq.ps1 -EventJson '{"post_type":"message","message_type":"group","group_id":123,"unread":3,"sender":{"card":"小明"}}'
```

任务栏出现 `QQ 3 条未读`，悬停面板显示明细行。`ttl` 默认 30 秒，要更长就加 `-Ttl 300`。

行为约定：心跳/通知/请求类事件、没有未读数、JSON 解析失败时**安静退出 0**，不会打扰调用方。

---

## 场景三：MCP 工具 / 任意程序 exec 一行

任何能执行命令的工具（MCP server、CI 脚本、Node/Python 程序）都只需要拼一条命令行，不用实现管道客户端：

```text
bin\VibeBar.Cli.exe --segment id=mcp.job text="跑测试中" label=MCP color=waiting ttl=120
```

Node.js：

```javascript
const { execFileSync } = require('child_process');
execFileSync('bin\\VibeBar.Cli.exe',
  ['--segment', 'id=mcp.job', 'text=跑测试中', 'label=MCP', 'color=waiting', 'ttl=120']);
```

Python：

```python
import subprocess
subprocess.run([r"bin\VibeBar.Cli.exe", "--segment", "id=mcp.job",
                "text=跑测试中", "label=MCP", "color=waiting", "ttl=120"], check=False)
```

要点：

- 参数用数组形式传，别拼成一个大字符串，中文和空格就不用自己加引号。
- `id` 建议 `<来源>.<键>`（如 `mcp.job`、`napcat.qq`），后续用 `--segment-clear id=mcp` 一次清干净。
- 任务结束记得显式清理：`--segment-remove id=mcp.job`，别只依赖 TTL。
- 长时间任务用较大的 `ttl` 并周期性重推（每次 `set` 都会刷新到期时间）。
