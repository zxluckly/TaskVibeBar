# VibeBar 项目上下文（给后续开发 / AI 会话）

## 一、项目是什么

VibeBar 是驻留在 Windows 任务栏通知区左侧的轻量信息条，默认显示：

- **额度**：DeepSeek 余额 + 当前峰/谷时段
- **Git**：当前仓库分支、脏文件、ahead/behind、冲突
- **Claude 会话**：通过 Claude Code Hooks 显示空闲/思考/工具/等待确认/完成

另有可配置的显示区域（靠左/靠右吸附、偏移、最大宽度、模块显隐）与悬停背景（颜色/透明度）。

## 二、构建与运行

```bat
build.bat
```

- 使用系统自带 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 编译，**不需要 dotnet SDK / MSBuild / XAML**。
- 产物：`bin\VibeBar.exe`（WPF 界面）与 `bin\VibeBar.Cli.exe`（Hook/statusLine 事件桥）。
- 语言固定 **C# 5**（csc 4.0.30319）：禁止字符串插值、`?.`、`nameof`、表达式体成员、元组、自动属性初始化器。
- UI 全部代码构建，不允许新增 `.xaml`。

## 三、目录结构

```
src/
├─ App/                 入口、总装配、设置/凭证、日志、Win32
│  ├─ Program.cs
│  ├─ AppController.cs  ← 唯一装配点：Provider、Presenter、Registry、托盘、面板
│  ├─ SettingsStore.cs  ← settings.json schema 与迁移
│  ├─ Secrets.cs        ← DPAPI 凭证
│  ├─ AutoStart.cs      ← HKCU Run
│  ├─ Json.cs / Log.cs / NativeMethods.cs / Theme.cs
├─ Models/              数据契约
│  ├─ Models.cs         ← Quota/Git/AgentSnapshot、ProviderResult、枚举
│  └─ SegmentVisual.cs  ← 段/详情/动作视图模型
├─ Ui/
│  ├─ TaskbarHost.cs    ← 任务栏子窗口 + 定位/吸附/宽度
│  ├─ TaskbarStrip.cs   ← 段布局、悬停、Pulse、折叠
│  ├─ DetailPanel.cs    ← 独立置顶无激活详情面板
│  ├─ TrayController.cs ← 托盘菜单
│  ├─ SettingsWindow.cs ← 代码构建设置窗口
│  └─ UiTheme.cs
├─ Providers/           ← 只取数，不引用 System.Windows.*
│  ├─ IProvider.cs / ProviderScheduler.cs
│  ├─ DeepSeekQuotaProvider.cs
│  ├─ GitProvider.cs
│  └─ AgentProvider.cs
├─ Segments/
│  ├─ ISegmentPresenter.cs ← 段契约 + ISegmentActionHost + 基类
│  ├─ SegmentRegistry.cs   ← 注册、排序、显隐、构建
│  ├─ SegmentDescriptor.cs ← 设置窗口动态开关的描述
│  ├─ PeakValley.cs
│  ├─ SegmentFactory.cs    ← 现有三个段的格式化/视图构建
│  └─ Presenters/
│     ├─ QuotaSegmentPresenter.cs
│     ├─ GitSegmentPresenter.cs
│     └─ AgentSegmentPresenter.cs
├─ EventBus/            ← Claude Code 事件桥
│  ├─ EventProtocol.cs  ← 与 CLI 共享，禁止引用 WPF
│  ├─ EventServer.cs    ← 命名管道服务端（当前用户 ACL）
│  └─ HookInstaller.cs  ← settings.json 合并/备份/卸载
└─ Cli/
   └─ CliMain.cs        ← VibeBar.Cli.exe（--hook）
```

## 四、新增一个信息显示（标准流程）

以新增「端口 / 本地服务」为例：

1. **数据契约**：在 `Models/Models.cs` 增加 `PortSnapshot`（或复用现有模型）。
2. **Provider**：在 `Providers/` 新增 `PortProvider : IProvider`：
   - 只取数、异步、带超时/取消；
   - 产出 `ProviderResult`，失败要有中文错误，不显示 0；
   - 在 `AppController.CreateProviders` 注册到 `ProviderScheduler`。
3. **Presenter**：在 `Segments/Presenters/` 新增 `PortSegmentPresenter : SegmentPresenterBase`：
   - 构造参数 `ISegmentActionHost actions`；
   - `BuildVisual(privacy)` 返回 `SegmentVisual`；
   - `BuildDetail(privacy)` 返回 `DetailSection`，动作通过 `actions` 执行；
   - `Update(PortSnapshot)` 由 AppController 在 Provider 结果到达时调用。
4. **注册**：在 `AppController.CreateUi` 中：
   ```csharp
   _segments.Register(new PortSegmentPresenter(this));
   _segments.EnsureSettingDefaults(_settings);
   ```
   `EnsureSettingDefaults` 会自动把新段加入 `ui.segments`（默认启用），设置窗口会**自动出现**开关，无需改 UI。
5. **Provider 结果分发**：在 `ApplyProviderResult` 中按 `ProviderId` 调用对应 Presenter 的 `Update`。
6. **文档/验收**：更新 README 功能表与验收用例；运行 `build.bat`。

> 禁止在 `TaskbarStrip`/`DetailPanel` 里写死某个业务段的文案；一切段内容都从 Presenter 来。

## 五、架构约束

- **Provider 不引用 `System.Windows.*`**；UI 不直接调 Provider。
- 段排序/显隐统一走 `SegmentRegistry` + `UiSettings.Segments`，不要再加 `showXxx` 布尔字段。
- 新增配置键必须：读取有默认值、写入保留未知键、非法值回退。
- IPC v1 冻结：`{"v":1,"kind":"hook","ts":"...","payload":{...}}`；CLI 静默失败。
- Hook 命令的 `command` **不能带引号**（Claude Code 按可执行文件名解析）。
- `git status` 必须带 `GIT_OPTIONAL_LOCKS=0`，否则会写回 `.git/index` 触发监听自激循环。
- 任务栏宿主：`Opacity` 始终为 1、`OnSourceInitialized` 阶段 `SetParent`、`WS_EX_NOACTIVATE`、DPI 用 `VisualTreeHelper.GetDpi` 换算。
- 详情面板必须是独立顶层无激活窗口，不能 `SetParent` 到任务栏。
- 凭证只走 DPAPI；日志禁止出现 Key/Token。

## 六、关键文件地图

| 想改什么 | 去哪里 |
|---|---|
| 任务栏位置/吸附/宽度 | `Ui/TaskbarHost.cs` + `App/SettingsStore.cs` 的 `UiSettings` |
| 段外观/悬停/折叠 | `Ui/TaskbarStrip.cs` |
| 面板布局/按钮 | `Ui/DetailPanel.cs` + 对应 Presenter |
| 额度取数与错误 | `Providers/DeepSeekQuotaProvider.cs` |
| Git 取数与监听 | `Providers/GitProvider.cs` |
| Claude 会话状态 | `Providers/AgentProvider.cs` + `EventBus/*` |
| 设置窗口 | `Ui/SettingsWindow.cs` |
| 配置 schema/迁移 | `App/SettingsStore.cs` |
| 新增信息显示 | 按第四节 |

## 七、当前状态

已完成：T1 骨架/任务栏宿主、T2 DeepSeek 额度与峰谷、T3 Git、T4 Hooks/会话、T5 面板/隐私/自启/折叠、T6 显示区域与悬停背景。

待办（v1）：Claude Code `statusLine` 上下文/模型显示、端口/本地服务段、PR/CI、自定义命令段、多显示器/DPI 完整验收。
