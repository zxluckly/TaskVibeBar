# T2 主 Agent 复核记录

日期：2026-10-22
结论：**通过**。真实 DeepSeek 余额已接入，峰谷、隐私、设置窗口与资源指标通过主 Agent 桌面验收。T3 可以开始。

## 一、真实数据源验收

| 项 | 结果 |
|---|---|
| 真实 Key 请求 | `HTTP 200`，约 0.2–0.6s；日志无 Key、无 `Bearer`、无 `sk-` |
| 段显示 | `¥xx.xx 谷`（当前 19:4x 为谷时），格式与颜色正确；余额低于默认预警线 ¥20 时为 Warning |
| 面板 | `DeepSeek / 余额 / 赠送余额 / 充值余额 / 当前谷时 · 距峰时 13h14m / 峰时规则 / 最后更新 / 数据来源`，均 `IsOffscreen=False` |
| 隐私模式 | 段 `¥••• 谷`，面板 `余额：¥•••`、`赠送余额：¥•••`、`充值余额：¥•••` |
| 无 Key | 段 `额度 —`，面板显示 `未配置 API Key`；首次启动自动打开「VibeBar 设置」 |
| 关闭设置窗口 | 宿主仍在 `1598,1109–1747,1147` 可见，未误隐藏 |
| 资源 | 60s 空闲 CPU **0.089%**（12 核），工作集 **87.7MB**，60s 内存变化 -0.3MB |

> 真实余额数值不写入仓库；测试使用 `readKeyFromClaudeSettings=true`，从 Claude Code 配置在内存中读取，不落盘。

## 二、主 Agent 预检修正

1. **刷新间隔 schema**：新增 `quota.refreshSeconds`（默认 60，60–3600），不再占用 `git.pollSeconds`。设置窗口、SettingsStore、AppController、Provider 热更新均已对齐。
2. **峰谷性能**：`PeakValley` 改为候选边界算法（未来 9 个本地日的午夜 + 峰窗 start/end），单次最多约 12 个候选点；10,000 次 Evaluate 实测 60ms，1s 定时器下 CPU 实测 0.089%。
3. **峰时摘要**：面板摘要由当前 `PeakValleySettings` 动态生成，非法配置回退默认文案。

## 三、主 Agent 修正

- `SettingsWindow` 的刷新间隔下拉框移除了 30 秒选项：额度最小间隔为 60s，保留 30 会造成「选中 30、保存变 60」的误导。保留 60/120/300，并支持 settings.json 中的其他合法值。

## 四、子 Agent 自测

- 仓库外临时测试程序 `PASSED=98 FAILED=0`，覆盖峰谷全部边界、周末→周一 09:00、跨午夜、非法配置、DeepSeek 成功/空余额/缺字段/脏 JSON、Base URL 规范化、连续失败 3 次 Stale、SettingsStore 未知键与 `refreshSeconds`、调度器首次延迟/隔离/刷新/退避/超时/禁止重入。
- 构建 0 error；剩余 29 个 CS0649 为 T3/T4/T5 尚未使用的字段，预期。

## 五、仍需人工/后续验收

- 真实 401/403 与网络超时的 UI 文案（代码映射已复核，未用真实错误 Key 发请求）。
- 托盘「设置」点击、设置窗口保存/取消/浏览目录的实际交互。
- Explorer 重启、任务栏自动隐藏/全屏、125%/150%/200% DPI、多显示器。
- 修改刷新间隔后的真实请求间隔。
- T3/T4 接入后面板多区块排版与宽度。

## 六、下一步

T3：Git Provider（分支、脏文件、ahead/behind、冲突、worktree、FileSystemWatcher）。
