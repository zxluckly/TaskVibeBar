using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace VibeBar
{
    /// <summary>
    /// 总装配：设置、Provider 调度、任务栏宿主、详情面板、托盘与生命周期。
    /// Provider 结果在后台线程产生，统一经 Dispatcher 回到 UI。
    /// </summary>
    internal sealed class AppController : ISegmentActionHost
    {
        private readonly Application _app;
        private readonly Dispatcher _dispatcher;
        private readonly SettingsStore _settingsStore;
        private readonly TaskbarWatcher _watcher;

        private AppSettings _settings;
        private TaskbarStrip _strip;
        private TaskbarHost _host;
        private DetailPanel _panel;
        private TrayController _tray;
        private ProviderScheduler _scheduler;
        private DeepSeekQuotaProvider _quotaProvider;
        private GitProvider _gitProvider;
        private QuotaSnapshot _quotaSnapshot;
        private AgentProvider _agentProvider;
        private EventServer _eventServer;
        private SegmentRegistry _segments;
        private QuotaSegmentPresenter _quotaPresenter;
        private GitSegmentPresenter _gitPresenter;
        private AgentSegmentPresenter _agentPresenter;
        private PortSegmentPresenter _portPresenter;
        private PrSegmentPresenter _prPresenter;
        private PortProvider _portProvider;
        private PrProvider _prProvider;
        private readonly Dictionary<string, CustomCommandProvider> _customProviders =
            new Dictionary<string, CustomCommandProvider>();
        private readonly Dictionary<string, CustomCommandSegmentPresenter> _customPresenters =
            new Dictionary<string, CustomCommandSegmentPresenter>();
        private DispatcherTimer _clockTimer;
        private SettingsWindow _settingsWindow;
        private string _visualSignature = string.Empty;
        private string _detailSignature = string.Empty;
        private int _pinnedKind = -1;
        private bool _initialSettingsCheckDone;
        private bool _exiting;

        internal AppController(Application app)
        {
            if (app == null)
            {
                throw new ArgumentNullException("app");
            }

            _app = app;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _settingsStore = new SettingsStore();
            _settings = _settingsStore.Load();

            SegmentFactory.Configure(
                _settings.Quota.WarnBelow,
                _settings.Quota.CriticalBelow);
            SegmentFactory.ConfigurePeakRules(_settings.Quota.PeakValley);

            Theme.Initialize(Dispatcher.CurrentDispatcher);
            Theme.Changed += OnThemeChanged;

            _watcher = new TaskbarWatcher();
            _watcher.TaskbarCreated += OnTaskbarCreated;

            CreateUi();
            CreateProviders();
            StartClock();

            Log.Write("VibeBar 启动，任务栏显示=" + _settings.General.ShowTaskbar);

            _dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(delegate
                {
                    if (_exiting)
                    {
                        return;
                    }

                    ApplyVisibility(_settings.General.ShowTaskbar);
                    MaybeShowInitialSettings();
                }));
        }

        private void CreateUi()
        {
            _segments = new SegmentRegistry();
            _quotaPresenter = new QuotaSegmentPresenter(this);
            _gitPresenter = new GitSegmentPresenter(this);
            _agentPresenter = new AgentSegmentPresenter();
            _portPresenter = new PortSegmentPresenter(this);
            _prPresenter = new PrSegmentPresenter(this);
            _segments.Register(_quotaPresenter);
            _segments.Register(_gitPresenter);
            _segments.Register(_agentPresenter);
            _segments.Register(_portPresenter);
            _segments.Register(_prPresenter);
            SyncCustomSegments(false);
            _segments.EnsureSettingDefaults(_settings);

            _strip = new TaskbarStrip(BuildVisuals());
            _strip.HoverChanged += OnStripHoverChanged;
            _strip.SegmentClicked += OnSegmentClicked;
            _strip.SetHoverStyle(
                _settings.Ui.HoverColor,
                _settings.Ui.HoverOpacity);

            _panel = new DetailPanel();
            _panel.SettingsRequested += OnSettingsRequested;
            _panel.SetSections(BuildDetailSections());

            _host = new TaskbarHost(_strip);
            _host.SetWidthLimits(_settings.Ui.MaxWidth, _settings.Ui.MinWidth);
            _host.SetAnchor(_settings.Ui.IsLeftAnchor);
            _host.SetOffsetX(_settings.Ui.OffsetX);
            _host.SetMonitor(_settings.Ui.IsSecondaryMonitor);

            _tray = new TrayController();
            _tray.ToggleVisibilityRequested += OnToggleVisibilityRequested;
            _tray.RefreshAllRequested += OnRefreshAllRequested;
            _tray.PrivacyModeRequested += OnPrivacyModeRequested;
            _tray.AutoStartRequested += OnAutoStartRequested;
            _tray.SettingsRequested += OnSettingsRequested;
            _tray.OpenDataFolderRequested += OnOpenDataFolderRequested;
            _tray.MenuOpening += OnTrayMenuOpening;
            _tray.ExitRequested += OnExitRequested;
            _tray.UpdateVisibility(_settings.General.ShowTaskbar);
            _tray.UpdatePrivacy(_settings.General.PrivacyMode);

            // 以注册表实际状态为准，避免程序移动目录后勾选状态与系统不一致。
            bool autoStart = AutoStart.IsEnabled();
            _tray.UpdateAutoStart(autoStart);
            if (_settings.General.AutoStart != autoStart)
            {
                _settings.General.AutoStart = autoStart;
                SaveSettings();
            }
        }

        private void CreateProviders()
        {
            _quotaProvider = new DeepSeekQuotaProvider(
                _settings.Quota.BaseUrl,
                _settings.Quota.ReadKeyFromClaudeSettings,
                QuotaPollSeconds());

            _gitProvider = new GitProvider(
                _settings.Git.RepoPath,
                _settings.Git.IncludeUntracked,
                _settings.Git.PollSeconds);
            _gitProvider.RefreshSuggested += OnGitRefreshSuggested;

            if (_settings.Agent.Enabled)
            {
                _agentProvider = new AgentProvider(_settings.Agent.StaleMinutes);
                _agentProvider.RefreshSuggested += OnAgentRefreshSuggested;
            }
            else
            {
                Log.Write("Claude 会话事件已禁用");
            }

            _portProvider = new PortProvider(
                _settings.Ports.WatchPorts,
                _settings.Ports.PollSeconds);
            _prProvider = new PrProvider(
                _settings.Git.RepoPath,
                _settings.Pr.PollSeconds);

            _scheduler = new ProviderScheduler();
            _scheduler.ResultAvailable += OnProviderResult;
            _scheduler.Register(_quotaProvider);
            _scheduler.Register(_gitProvider);
            if (_agentProvider != null)
            {
                _scheduler.Register(_agentProvider);
            }

            _scheduler.Register(_portProvider);
            _scheduler.Register(_prProvider);

            // 自定义命令段的 Provider 依赖调度器，必须在 CreateUi 之后补注册。
            SyncCustomSegments(true);

            _scheduler.Start();

            if (_agentProvider != null)
            {
                _eventServer = new EventServer(_agentProvider);
                _eventServer.Start();
            }
        }

        /// <summary>
        /// 按 settings.custom.commands 同步自定义命令段：增删 Presenter，
        /// 需要时同时增删 Provider。设置保存后调用即可热生效。
        /// </summary>
        private void SyncCustomSegments(bool includeProviders)
        {
            if (_segments == null)
            {
                return;
            }

            List<CustomCommandSettings> desired = GetEnabledCustomCommands();

            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, CustomCommandSegmentPresenter> pair
                in _customPresenters)
            {
                if (!ContainsCommand(desired, pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }

            for (int index = 0; index < stale.Count; index++)
            {
                string id = stale[index];
                _segments.Unregister(id);
                _customPresenters.Remove(id);
                if (includeProviders)
                {
                    RemoveCustomProvider(id);
                }
            }

            for (int index = 0; index < desired.Count; index++)
            {
                CustomCommandSettings command = desired[index];
                CustomCommandSegmentPresenter presenter;
                if (!_customPresenters.TryGetValue(command.Id, out presenter))
                {
                    presenter = new CustomCommandSegmentPresenter(
                        command.Id,
                        command.Label,
                        command.Command,
                        command.Url,
                        60 + index,
                        this);
                    _segments.Register(presenter);
                    _customPresenters[command.Id] = presenter;
                }
                else
                {
                    CustomCommandProvider existing;
                    if (_customProviders.TryGetValue(command.Id, out existing))
                    {
                        bool changed = !string.Equals(
                                existing.Command,
                                command.Command,
                                StringComparison.Ordinal)
                            || !string.Equals(
                                existing.Label,
                                command.Label,
                                StringComparison.Ordinal)
                            || !string.Equals(
                                existing.Url,
                                command.Url,
                                StringComparison.Ordinal);
                        existing.UpdateSettings(command);

                        if (changed)
                        {
                            // 段文案与命令都是只读字段，重建 Presenter 最简单可靠。
                            _segments.Unregister(command.Id);
                            _customPresenters.Remove(command.Id);
                            presenter = new CustomCommandSegmentPresenter(
                                command.Id,
                                command.Label,
                                command.Command,
                                command.Url,
                                60 + index,
                                this);
                            _segments.Register(presenter);
                            _customPresenters[command.Id] = presenter;
                        }
                    }
                }

                if (includeProviders)
                {
                    EnsureCustomProvider(command);
                }
            }
        }

        private void EnsureCustomProvider(CustomCommandSettings command)
        {
            if (_scheduler == null || command == null)
            {
                return;
            }

            CustomCommandProvider provider;
            if (_customProviders.TryGetValue(command.Id, out provider))
            {
                provider.UpdateSettings(command);
                return;
            }

            provider = new CustomCommandProvider(command);
            _customProviders[command.Id] = provider;
            _scheduler.Register(provider);
        }

        private void RemoveCustomProvider(string segmentId)
        {
            CustomCommandProvider provider;
            if (!_customProviders.TryGetValue(segmentId, out provider))
            {
                return;
            }

            _customProviders.Remove(segmentId);
            if (_scheduler != null)
            {
                _scheduler.Unregister(provider.Id);
            }
            else
            {
                provider.Dispose();
            }
        }

        private List<CustomCommandSettings> GetEnabledCustomCommands()
        {
            List<CustomCommandSettings> result = new List<CustomCommandSettings>();
            if (_settings == null
                || _settings.Custom == null
                || _settings.Custom.Commands == null)
            {
                return result;
            }

            for (int index = 0; index < _settings.Custom.Commands.Count; index++)
            {
                CustomCommandSettings command = _settings.Custom.Commands[index];
                if (command != null
                    && command.Enabled
                    && !string.IsNullOrEmpty(command.Command))
                {
                    result.Add(command);
                }
            }

            return result;
        }

        private static bool ContainsCommand(
            List<CustomCommandSettings> commands,
            string id)
        {
            for (int index = 0; index < commands.Count; index++)
            {
                if (string.Equals(commands[index].Id, id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void OnAgentRefreshSuggested()
        {
            if (_exiting)
            {
                return;
            }

            ProviderScheduler scheduler = _scheduler;
            if (scheduler == null)
            {
                return;
            }

            try
            {
                scheduler.Refresh(AgentProvider.ProviderIdValue);
            }
            catch (Exception exception)
            {
                Log.Write("Agent 刷新请求失败：" + exception.GetType().Name);
            }
        }

        private void RefreshAgentCache()
        {
            if (_agentProvider == null || _agentPresenter == null)
            {
                return;
            }

            _agentPresenter.Update(
                _agentProvider.GetAggregateSnapshot(),
                _agentProvider.GetSessionSnapshots());
        }

        private void OnGitRefreshSuggested()
        {
            if (_exiting)
            {
                return;
            }

            ProviderScheduler scheduler = _scheduler;
            if (scheduler == null)
            {
                return;
            }

            try
            {
                scheduler.Refresh(GitProvider.ProviderIdValue);
            }
            catch (Exception exception)
            {
                Log.Write("Git 刷新请求失败：" + exception.GetType().Name);
            }
        }

        private void StartClock()
        {
            _clockTimer = new DispatcherTimer();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += OnClockTick;
            _clockTimer.Start();
        }

        private int QuotaPollSeconds()
        {
            int seconds = _settings.Quota.RefreshSeconds;
            if (seconds < 60)
            {
                seconds = 60;
            }
            else if (seconds > 3600)
            {
                seconds = 3600;
            }

            return seconds;
        }

        private SegmentVisual[] BuildVisuals()
        {
            if (_segments == null)
            {
                return new SegmentVisual[0];
            }

            return _segments.BuildVisuals(
                _settings,
                _settings.General.PrivacyMode);
        }

        private DetailSection[] BuildDetailSections()
        {
            if (_segments == null)
            {
                return new DetailSection[0];
            }

            return _segments.BuildDetails(
                _settings,
                _settings.General.PrivacyMode);
        }

        public void RefreshProvider(string providerId)
        {
            ProviderScheduler scheduler = _scheduler;
            if (scheduler == null || string.IsNullOrEmpty(providerId))
            {
                return;
            }

            try
            {
                scheduler.Refresh(providerId);
            }
            catch (Exception exception)
            {
                Log.Write("手动刷新失败：" + exception.GetType().Name);
            }
        }

        public void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(url);
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch (Exception exception)
            {
                Log.Error("打开链接失败", exception);
                if (_tray != null)
                {
                    _tray.ShowError("无法打开链接，详见日志");
                }
            }
        }

        public void ShowSettings()
        {
            if (_exiting)
            {
                return;
            }

            _dispatcher.BeginInvoke(new Action(ShowSettingsWindow));
        }

        public void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(path))
                {
                    Log.Write("打开目录失败：目录不存在");
                    if (_tray != null)
                    {
                        _tray.ShowError("目录不存在");
                    }

                    return;
                }

                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception exception)
            {
                Log.Error("打开目录失败", exception);
                if (_tray != null)
                {
                    _tray.ShowError("无法打开目录，详见日志");
                }
            }
        }

        public void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception exception)
            {
                Log.Error("复制到剪贴板失败", exception);
                if (_tray != null)
                {
                    _tray.ShowError("复制失败，详见日志");
                }
            }
        }

        private void OnClockTick(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            if (_quotaSnapshot != null)
            {
                RecomputePeak(_quotaSnapshot);
            }

            if (_agentProvider != null)
            {
                try
                {
                    if (_agentProvider.Tick(DateTime.UtcNow))
                    {
                        RefreshAgentCache();
                    }
                }
                catch (Exception exception)
                {
                    Log.Write("推进 Agent 会话状态失败：" + exception.GetType().Name);
                }
            }

            RefreshVisuals(false);

            if (_host != null && !_host.IsDestroyed
                && _settings.General.ShowTaskbar)
            {
                try
                {
                    _host.RefreshLayout();
                }
                catch (Exception exception)
                {
                    Log.Write("刷新任务栏布局失败：" + exception.GetType().Name);
                }
            }
        }

        private void RecomputePeak(QuotaSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            bool isOffPeak;
            DateTime nextTransitionUtc;
            PeakValley.Evaluate(
                DateTime.UtcNow,
                _settings.Quota.PeakValley,
                out isOffPeak,
                out nextTransitionUtc);

            snapshot.IsOffPeak = isOffPeak;
            snapshot.NextTransitionUtc = nextTransitionUtc;
        }

        private void RefreshVisuals(bool force)
        {
            if (_exiting || _strip == null || _panel == null)
            {
                return;
            }

            SegmentVisual[] visuals = BuildVisuals();
            string visualSignature = ComputeVisualSignature(visuals);
            if (force || visualSignature != _visualSignature)
            {
                _visualSignature = visualSignature;
                _strip.SetVisuals(visuals);
            }

            DetailSection[] sections = BuildDetailSections();
            string detailSignature = ComputeDetailSignature(sections);
            if (force || detailSignature != _detailSignature)
            {
                _detailSignature = detailSignature;
                _panel.SetSections(sections);
            }
        }

        private static string ComputeVisualSignature(SegmentVisual[] visuals)
        {
            StringBuilder builder = new StringBuilder();
            if (visuals == null)
            {
                return string.Empty;
            }

            for (int index = 0; index < visuals.Length; index++)
            {
                SegmentVisual visual = visuals[index];
                if (visual == null)
                {
                    continue;
                }

                builder.Append((int)visual.Kind);
                builder.Append(':');
                builder.Append(visual.Glyph);
                builder.Append(':');
                builder.Append(visual.Text);
                builder.Append(':');
                builder.Append((int)visual.Role);
                builder.Append(':');
                builder.Append(visual.Pulse ? '1' : '0');
                builder.Append(';');
            }

            return builder.ToString();
        }

        private static string ComputeDetailSignature(DetailSection[] sections)
        {
            StringBuilder builder = new StringBuilder();
            if (sections == null)
            {
                return string.Empty;
            }

            for (int index = 0; index < sections.Length; index++)
            {
                DetailSection section = sections[index];
                if (section == null)
                {
                    continue;
                }

                builder.Append(section.Title);
                builder.Append('\u001e');
                if (section.Lines != null)
                {
                    for (int lineIndex = 0; lineIndex < section.Lines.Length; lineIndex++)
                    {
                        builder.Append(section.Lines[lineIndex]);
                        builder.Append('\u001f');
                    }
                }

                builder.Append('\u001d');
            }

            return builder.ToString();
        }

        private void OnProviderResult(ProviderResult result)
        {
            if (_exiting)
            {
                return;
            }

            try
            {
                _dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(delegate
                    {
                        ApplyProviderResult(result);
                    }));
            }
            catch (InvalidOperationException)
            {
                // 应用正在退出，消息循环已不可用。
            }
        }

        private void ApplyProviderResult(ProviderResult result)
        {
            if (_exiting || result == null)
            {
                return;
            }

            if (string.Equals(
                result.ProviderId,
                DeepSeekQuotaProvider.ProviderIdValue,
                StringComparison.Ordinal))
            {
                ApplyQuotaResult(result);
                RefreshVisuals(true);
                return;
            }

            if (string.Equals(
                result.ProviderId,
                GitProvider.ProviderIdValue,
                StringComparison.Ordinal))
            {
                ApplyGitResult(result);
                RefreshVisuals(true);
                return;
            }

            if (string.Equals(
                result.ProviderId,
                AgentProvider.ProviderIdValue,
                StringComparison.Ordinal))
            {
                ApplyAgentResult(result);
                RefreshVisuals(true);
                return;
            }

            if (string.Equals(
                result.ProviderId,
                PortProvider.ProviderIdValue,
                StringComparison.Ordinal))
            {
                ApplyPortResult(result);
                RefreshVisuals(true);
                return;
            }

            if (string.Equals(
                result.ProviderId,
                PrProvider.ProviderIdValue,
                StringComparison.Ordinal))
            {
                ApplyPrResult(result);
                RefreshVisuals(true);
                return;
            }

            if (result.ProviderId != null
                && result.ProviderId.StartsWith(
                    CustomCommandProvider.ProviderIdPrefix,
                    StringComparison.Ordinal))
            {
                ApplyCustomResult(result);
                RefreshVisuals(true);
            }
        }

        private void ApplyPortResult(ProviderResult result)
        {
            if (_portPresenter == null)
            {
                return;
            }

            PortSnapshot snapshot = result.Snapshot as PortSnapshot;
            if (snapshot == null)
            {
                snapshot = new PortSnapshot();
                snapshot.Ports = new PortEntry[0];
                snapshot.Health = result.Health == SnapshotHealth.Live
                    ? SnapshotHealth.Error
                    : result.Health;
                snapshot.Error = string.IsNullOrEmpty(result.Error)
                    ? "端口探测失败"
                    : result.Error;
                snapshot.UpdatedUtc = result.UpdatedUtc;
            }

            _portPresenter.Update(snapshot);
        }

        private void ApplyPrResult(ProviderResult result)
        {
            if (_prPresenter == null)
            {
                return;
            }

            PrSnapshot snapshot = result.Snapshot as PrSnapshot;
            if (snapshot == null)
            {
                snapshot = new PrSnapshot();
                snapshot.Exists = false;
                snapshot.Health = result.Health == SnapshotHealth.Live
                    ? SnapshotHealth.Error
                    : result.Health;
                snapshot.Error = string.IsNullOrEmpty(result.Error)
                    ? "PR 数据不可用"
                    : result.Error;
                snapshot.UpdatedUtc = result.UpdatedUtc;
            }

            _prPresenter.Update(snapshot);
        }

        private void ApplyCustomResult(ProviderResult result)
        {
            if (result.ProviderId == null || _customPresenters.Count == 0)
            {
                return;
            }

            string id = result.ProviderId.Substring(
                CustomCommandProvider.ProviderIdPrefix.Length);
            CustomCommandSegmentPresenter presenter;
            if (!_customPresenters.TryGetValue(id, out presenter))
            {
                return;
            }

            CustomCommandSnapshot snapshot = result.Snapshot as CustomCommandSnapshot;
            if (snapshot == null)
            {
                snapshot = new CustomCommandSnapshot();
                snapshot.Id = id;
                snapshot.Health = SnapshotHealth.Error;
                snapshot.Error = string.IsNullOrEmpty(result.Error)
                    ? "命令执行失败"
                    : result.Error;
                snapshot.UpdatedUtc = result.UpdatedUtc;
            }

            presenter.Update(snapshot);
        }

        private void ApplyAgentResult(ProviderResult result)
        {
            if (_agentPresenter == null)
            {
                return;
            }

            AgentSnapshot aggregate = result.Snapshot as AgentSnapshot;
            AgentSnapshot[] sessions = _agentProvider == null
                ? new AgentSnapshot[0]
                : _agentProvider.GetSessionSnapshots();
            _agentPresenter.Update(aggregate, sessions);
        }

        private void ApplyQuotaResult(ProviderResult result)
        {
            QuotaSnapshot snapshot = result.Snapshot as QuotaSnapshot;
            if (snapshot != null)
            {
                _quotaSnapshot = snapshot;
            }
            else
            {
                if (_quotaSnapshot == null)
                {
                    _quotaSnapshot = CreateSchedulerErrorSnapshot(result.Error);
                }
                else
                {
                    _quotaSnapshot.Error = string.IsNullOrEmpty(result.Error)
                        ? "数据源异常"
                        : result.Error;
                    _quotaSnapshot.Health =
                        _quotaSnapshot.Health == SnapshotHealth.Stale
                            ? SnapshotHealth.Stale
                            : SnapshotHealth.Error;
                }
            }

            RecomputePeak(_quotaSnapshot);

            if (_quotaPresenter != null)
            {
                _quotaPresenter.Update(_quotaSnapshot);
            }
        }

        private void ApplyGitResult(ProviderResult result)
        {
            if (_gitPresenter == null)
            {
                return;
            }

            GitSnapshot snapshot = result.Snapshot as GitSnapshot;
            if (snapshot == null)
            {
                // 调度器兜底错误（超时/异常）没有快照，构造/复用错误态，
                // 段显示「Git —」，面板给出原因，绝不显示 0。
                snapshot = _gitPresenter.Snapshot;
                if (snapshot == null)
                {
                    snapshot = new GitSnapshot();
                    snapshot.RepoPath = _settings.Git.RepoPath == null
                        ? string.Empty
                        : _settings.Git.RepoPath;
                    snapshot.RepoName = GitProvider.GetRepoName(snapshot.RepoPath);
                    snapshot.Branch = string.Empty;
                    snapshot.Operation = string.Empty;
                }

                snapshot.Health = result.Health == SnapshotHealth.Live
                    ? SnapshotHealth.Error
                    : result.Health;
                snapshot.Error = string.IsNullOrEmpty(result.Error)
                    ? "数据源异常"
                    : result.Error;
                snapshot.UpdatedUtc = result.UpdatedUtc == default(DateTime)
                    ? DateTime.UtcNow
                    : result.UpdatedUtc;
            }

            _gitPresenter.Update(snapshot);
        }

        private static QuotaSnapshot CreateSchedulerErrorSnapshot(string error)
        {
            QuotaSnapshot snapshot = new QuotaSnapshot();
            snapshot.ProviderId = DeepSeekQuotaProvider.ProviderIdValue;
            snapshot.DisplayName = "DeepSeek";
            snapshot.Balance = null;
            snapshot.Currency = "CNY";
            snapshot.IsAvailable = false;
            snapshot.Health = SnapshotHealth.Error;
            snapshot.Source = SnapshotSource.Official;
            snapshot.Error = string.IsNullOrEmpty(error) ? "数据源异常" : error;
            snapshot.UpdatedUtc = DateTime.MinValue;
            return snapshot;
        }

        private void OnStripHoverChanged(bool hovered)
        {
            if (_exiting || !_settings.General.ShowTaskbar)
            {
                return;
            }

            if (hovered)
            {
                if (_host == null || _host.IsDestroyed)
                {
                    return;
                }

                _panel.ScheduleShow(_host.GetScreenBounds(), _host.TaskbarHandle);
            }
            else
            {
                _panel.ScheduleHide();
            }
        }

        private void OnSegmentClicked(SegmentKind kind)
        {
            if (_exiting || !_settings.General.ShowTaskbar
                || _panel == null || _host == null || _host.IsDestroyed)
            {
                return;
            }

            int kindValue = (int)kind;
            if (_panel.IsPinned && _pinnedKind == kindValue)
            {
                // 再次点击同一段：取消固定，之后仍遵循鼠标移开 300ms 收起。
                _panel.SetPinned(false);
                _pinnedKind = -1;
                return;
            }

            _panel.ShowPinned(_host.GetScreenBounds(), _host.TaskbarHandle);
            _pinnedKind = kindValue;
        }

        private void OnToggleVisibilityRequested(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            bool show = !_settings.General.ShowTaskbar;
            _settings.General.ShowTaskbar = show;
            SaveSettings();
            ApplyVisibility(show);
        }

        private void OnSettingsRequested(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            _dispatcher.BeginInvoke(new Action(ShowSettingsWindow));
        }

        private void OnRefreshAllRequested(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            ProviderScheduler scheduler = _scheduler;
            if (scheduler == null)
            {
                return;
            }

            try
            {
                scheduler.RefreshAll();
            }
            catch (Exception exception)
            {
                Log.Write("刷新全部失败：" + exception.GetType().Name);
            }
        }

        private void OnPrivacyModeRequested(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            _settings.General.PrivacyMode = !_settings.General.PrivacyMode;
            SaveSettings();
            _tray.UpdatePrivacy(_settings.General.PrivacyMode);
            RefreshVisuals(true);
        }

        private void OnAutoStartRequested(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            bool enabled = !AutoStart.IsEnabled();
            string error = AutoStart.SetEnabled(enabled);
            if (!string.IsNullOrEmpty(error))
            {
                Log.Write("设置开机自启失败：" + error);
                _tray.ShowError(error);
                _tray.UpdateAutoStart(AutoStart.IsEnabled());
                return;
            }

            bool actual = AutoStart.IsEnabled();
            _settings.General.AutoStart = actual;
            SaveSettings();
            _tray.UpdateAutoStart(actual);
        }

        private void OnOpenDataFolderRequested(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    "VibeBar");
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception exception)
            {
                Log.Error("打开数据目录失败", exception);
                _tray.ShowError("无法打开数据目录，详见日志");
            }
        }

        private void OnTrayMenuOpening(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            _tray.UpdateVisibility(_settings.General.ShowTaskbar);
            _tray.UpdatePrivacy(_settings.General.PrivacyMode);

            bool actual = AutoStart.IsEnabled();
            if (_settings.General.AutoStart != actual)
            {
                _settings.General.AutoStart = actual;
                SaveSettings();
            }

            _tray.UpdateAutoStart(actual);
        }

        private void MaybeShowInitialSettings()
        {
            if (_exiting || _initialSettingsCheckDone)
            {
                return;
            }

            _initialSettingsCheckDone = true;

            if (_settings.Quota.ReadKeyFromClaudeSettings)
            {
                return;
            }

            string key = null;
            try
            {
                key = Secrets.LoadApiKey();
            }
            catch (Exception exception)
            {
                Log.Write("读取 API Key 失败：" + exception.GetType().Name);
            }

            if (!string.IsNullOrEmpty(key))
            {
                Log.Debug("首次启动检查：已配置 API Key，跳过设置窗口");
                return;
            }

            Log.Write("首次启动检查：未配置 API Key，打开设置窗口");
            ShowSettingsWindow();
        }

        private void ShowSettingsWindow()
        {
            if (_exiting || _settingsWindow != null)
            {
                return;
            }

            SettingsWindow window = new SettingsWindow(
                _settings,
                _settingsStore,
                _segments == null
                    ? new List<SegmentDescriptor>()
                    : _segments.GetDescriptors());
            window.Saved += OnSettingsSaved;

            // 悬浮条在副屏时，设置窗口也在副屏居中，不要跳回主屏。
            if (_host != null && !_host.IsDestroyed)
            {
                window.PlaceOnMonitor(_host.GetScreenBounds());
            }

            _settingsWindow = window;

            // 设置窗口需要正常激活；临时取消面板置顶，避免遮挡。
            if (_panel != null)
            {
                _panel.SetTopmost(false);
            }

            try
            {
                window.ShowDialog();
            }
            catch (Exception exception)
            {
                Log.Error("打开设置窗口失败", exception);
            }
            finally
            {
                window.Saved -= OnSettingsSaved;
                _settingsWindow = null;

                if (_panel != null)
                {
                    _panel.SetTopmost(true);
                }
            }
        }

        private void OnSettingsSaved(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            ApplySettingsChange();
        }

        private void ApplySettingsChange()
        {
            UiSettings.Normalize(_settings.Ui);
            if (_segments != null)
            {
                _segments.EnsureSettingDefaults(_settings);
            }

            SegmentFactory.Configure(
                _settings.Quota.WarnBelow,
                _settings.Quota.CriticalBelow);
            SegmentFactory.ConfigurePeakRules(_settings.Quota.PeakValley);

            if (_quotaProvider != null)
            {
                _quotaProvider.UpdateSettings(
                    _settings.Quota.BaseUrl,
                    _settings.Quota.ReadKeyFromClaudeSettings,
                    QuotaPollSeconds());
            }

            if (_quotaSnapshot != null)
            {
                RecomputePeak(_quotaSnapshot);
            }

            if (_gitProvider != null)
            {
                _gitProvider.UpdateSettings(
                    _settings.Git.RepoPath,
                    _settings.Git.IncludeUntracked,
                    _settings.Git.PollSeconds);
            }

            if (_agentProvider != null)
            {
                _agentProvider.UpdateStaleMinutes(_settings.Agent.StaleMinutes);
            }

            if (_portProvider != null)
            {
                _portProvider.UpdateSettings(
                    _settings.Ports.WatchPorts,
                    _settings.Ports.PollSeconds);
            }

            if (_prProvider != null)
            {
                _prProvider.UpdateSettings(
                    _settings.Git.RepoPath,
                    _settings.Pr.PollSeconds);
            }

            // 自定义命令段按新配置增删，再补齐 ui.segments 默认项。
            SyncCustomSegments(true);
            if (_segments != null)
            {
                _segments.EnsureSettingDefaults(_settings);
            }

            if (_tray != null)
            {
                _tray.UpdatePrivacy(_settings.General.PrivacyMode);
            }

            if (_strip != null)
            {
                _strip.SetHoverStyle(
                    _settings.Ui.HoverColor,
                    _settings.Ui.HoverOpacity);
            }

            if (_host != null && !_host.IsDestroyed)
            {
                _host.SetWidthLimits(
                    _settings.Ui.MaxWidth,
                    _settings.Ui.MinWidth);
                _host.SetAnchor(_settings.Ui.IsLeftAnchor);
                _host.SetOffsetX(_settings.Ui.OffsetX);
                _host.SetMonitor(_settings.Ui.IsSecondaryMonitor);
                _host.RefreshLayout();
            }

            ApplyVisibility(_settings.General.ShowTaskbar);
            RefreshVisuals(true);

            if (_scheduler != null)
            {
                _scheduler.Refresh(DeepSeekQuotaProvider.ProviderIdValue);
                _scheduler.Refresh(GitProvider.ProviderIdValue);
                _scheduler.Refresh(PortProvider.ProviderIdValue);
                _scheduler.Refresh(PrProvider.ProviderIdValue);

                foreach (KeyValuePair<string, CustomCommandProvider> pair
                    in _customProviders)
                {
                    _scheduler.Refresh(pair.Value.Id);
                }
            }
        }

        private void ApplyVisibility(bool visible)
        {
            if (_exiting)
            {
                return;
            }

            if (_host == null || _host.IsDestroyed)
            {
                RecreateHost();
            }

            if (_host != null && !_host.IsDestroyed)
            {
                _host.SetVisible(visible);

                // Explorer 重启时 TaskbarCreated 可能先于旧窗口的销毁到达，
                // SetVisible 会在此刻才发现宿主已失效：重建后重试一次。
                if (_host.IsDestroyed)
                {
                    Log.Write("任务栏宿主已失效，重建后重试");
                    RecreateHost();
                    if (_host != null && !_host.IsDestroyed)
                    {
                        _host.SetVisible(visible);
                    }
                }
            }

            if (!visible)
            {
                _panel.HideNow();
                _pinnedKind = -1;
            }

            _tray.UpdateVisibility(visible);
        }

        private void RecreateHost()
        {
            TaskbarStrip strip = new TaskbarStrip(BuildVisuals());
            strip.HoverChanged += OnStripHoverChanged;
            strip.SegmentClicked += OnSegmentClicked;
            strip.SetHoverStyle(
                _settings.Ui.HoverColor,
                _settings.Ui.HoverOpacity);

            _strip = strip;
            _host = new TaskbarHost(strip);
            _host.SetWidthLimits(_settings.Ui.MaxWidth, _settings.Ui.MinWidth);
            _host.SetAnchor(_settings.Ui.IsLeftAnchor);
            _host.SetOffsetX(_settings.Ui.OffsetX);
            _host.SetMonitor(_settings.Ui.IsSecondaryMonitor);
            _panel.SetSections(BuildDetailSections());
            _visualSignature = string.Empty;
            _detailSignature = string.Empty;
        }

        private void OnTaskbarCreated(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            // Explorer 的 TaskbarCreated 可能先于旧子窗口的销毁消息到达，
            // 延后到空闲阶段再判断宿主是否已失效。
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(RecoverAfterTaskbarCreated));
        }

        private void RecoverAfterTaskbarCreated()
        {
            if (_exiting)
            {
                return;
            }

            _panel.HideNow();
            _pinnedKind = -1;

            if (_host == null || _host.IsDestroyed)
            {
                RecreateHost();
                Log.Write("Explorer 重启，已重建任务栏宿主");
            }
            else
            {
                _host.Reattach();
                Log.Write("Explorer 重启，已重新挂载任务栏宿主");
            }

            ApplyVisibility(_settings.General.ShowTaskbar);
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            if (_strip != null)
            {
                _strip.ApplyTheme();
            }

            if (_panel != null)
            {
                _panel.ApplyTheme();
            }
        }

        private void SaveSettings()
        {
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception exception)
            {
                Log.Error("保存设置失败", exception);
                if (_tray != null)
                {
                    _tray.ShowError("保存设置失败，详见日志");
                }
            }
        }

        internal void NotifyUnhandled(Exception exception)
        {
            Log.Exception("未处理异常", exception);
            if (_tray != null && !_exiting)
            {
                _tray.ShowError("VibeBar 遇到异常，已记录到日志");
            }
        }

        internal void ShowError(string message)
        {
            if (_tray != null && !_exiting)
            {
                _tray.ShowError(message);
            }
        }

        private void OnExitRequested(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;

            if (_clockTimer != null)
            {
                _clockTimer.Stop();
                _clockTimer.Tick -= OnClockTick;
                _clockTimer = null;
            }

            if (_settingsWindow != null)
            {
                try
                {
                    _settingsWindow.Close();
                }
                catch
                {
                    // 设置窗口可能已经关闭。
                }

                _settingsWindow = null;
            }

            if (_gitProvider != null)
            {
                _gitProvider.RefreshSuggested -= OnGitRefreshSuggested;
            }

            if (_agentProvider != null)
            {
                _agentProvider.RefreshSuggested -= OnAgentRefreshSuggested;
            }

            // 先停事件服务（后台线程可能仍在 Ingest），再释放调度器/
            // AgentProvider，避免退出阶段的回调访问已释放对象。
            if (_eventServer != null)
            {
                _eventServer.Dispose();
                _eventServer = null;
            }

            if (_scheduler != null)
            {
                _scheduler.ResultAvailable -= OnProviderResult;
                _scheduler.Dispose();
                _scheduler = null;
            }

            _quotaProvider = null;
            _quotaSnapshot = null;
            _gitProvider = null;
            _agentProvider = null;
            _portProvider = null;
            _prProvider = null;
            _customProviders.Clear();
            _quotaPresenter = null;
            _gitPresenter = null;
            _agentPresenter = null;
            _portPresenter = null;
            _prPresenter = null;
            _customPresenters.Clear();
            _segments = null;

            Theme.Changed -= OnThemeChanged;
            Theme.Shutdown();

            if (_panel != null)
            {
                _panel.SettingsRequested -= OnSettingsRequested;
                _panel.HideNow();
                _panel.DisposePanel();
                _panel = null;
            }

            if (_watcher != null)
            {
                _watcher.TaskbarCreated -= OnTaskbarCreated;
                _watcher.Dispose();
            }

            if (_tray != null)
            {
                _tray.ToggleVisibilityRequested -= OnToggleVisibilityRequested;
                _tray.RefreshAllRequested -= OnRefreshAllRequested;
                _tray.PrivacyModeRequested -= OnPrivacyModeRequested;
                _tray.AutoStartRequested -= OnAutoStartRequested;
                _tray.SettingsRequested -= OnSettingsRequested;
                _tray.OpenDataFolderRequested -= OnOpenDataFolderRequested;
                _tray.MenuOpening -= OnTrayMenuOpening;
                _tray.ExitRequested -= OnExitRequested;
                _tray.Dispose();
                _tray = null;
            }

            if (_host != null && !_host.IsDestroyed)
            {
                try
                {
                    _host.SetVisible(false);
                    _host.Close();
                }
                catch
                {
                    // Explorer 重建期间窗口可能已被销毁。
                }
            }

            if (_strip != null)
            {
                _strip.HoverChanged -= OnStripHoverChanged;
                _strip.SegmentClicked -= OnSegmentClicked;
            }

            _host = null;
            _strip = null;

            Log.Write("VibeBar 退出");
            _app.Shutdown();
        }
    }
}
