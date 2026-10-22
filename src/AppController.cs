using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace VibeBar
{
    /// <summary>
    /// 总装配：设置、Provider 调度、任务栏宿主、详情面板、托盘与生命周期。
    /// Provider 结果在后台线程产生，统一经 Dispatcher 回到 UI。
    /// </summary>
    internal sealed class AppController
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
        private QuotaSnapshot _quotaSnapshot;
        private DispatcherTimer _clockTimer;
        private SettingsWindow _settingsWindow;
        private string _visualSignature = string.Empty;
        private string _detailSignature = string.Empty;
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
            _strip = new TaskbarStrip(BuildVisuals());
            _strip.HoverChanged += OnStripHoverChanged;

            _panel = new DetailPanel();
            _panel.SetSections(BuildDetailSections());

            _host = new TaskbarHost(_strip);

            _tray = new TrayController();
            _tray.ToggleVisibilityRequested += OnToggleVisibilityRequested;
            _tray.SettingsRequested += OnSettingsRequested;
            _tray.ExitRequested += OnExitRequested;
            _tray.UpdateVisibility(_settings.General.ShowTaskbar);
        }

        private void CreateProviders()
        {
            _quotaProvider = new DeepSeekQuotaProvider(
                _settings.Quota.BaseUrl,
                _settings.Quota.ReadKeyFromClaudeSettings,
                QuotaPollSeconds());

            _scheduler = new ProviderScheduler();
            _scheduler.ResultAvailable += OnProviderResult;
            _scheduler.Register(_quotaProvider);
            _scheduler.Start();
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
            SegmentVisual[] visuals = new SegmentVisual[3];
            visuals[0] = SegmentFactory.CreateQuota(
                _quotaSnapshot,
                _settings.General.PrivacyMode);
            visuals[1] = SegmentFactory.CreateGitPlaceholder();
            visuals[2] = SegmentFactory.CreateAgentPlaceholder();
            return visuals;
        }

        private DetailSection[] BuildDetailSections()
        {
            if (_quotaSnapshot == null)
            {
                return new DetailSection[0];
            }

            return new DetailSection[]
            {
                SegmentFactory.CreateQuotaDetail(
                    _quotaSnapshot,
                    _settings.General.PrivacyMode)
            };
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

            RefreshVisuals(false);
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

            if (!string.Equals(
                result.ProviderId,
                DeepSeekQuotaProvider.ProviderIdValue,
                StringComparison.Ordinal))
            {
                return;
            }

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
            RefreshVisuals(true);
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

                _panel.ScheduleShow(_host.GetScreenBounds());
            }
            else
            {
                _panel.ScheduleHide();
            }
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
                return;
            }

            ShowSettingsWindow();
        }

        private void ShowSettingsWindow()
        {
            if (_exiting || _settingsWindow != null)
            {
                return;
            }

            SettingsWindow window = new SettingsWindow(_settings, _settingsStore);
            window.Saved += OnSettingsSaved;
            _settingsWindow = window;

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

            ApplyVisibility(_settings.General.ShowTaskbar);
            RefreshVisuals(true);

            if (_scheduler != null)
            {
                _scheduler.Refresh(DeepSeekQuotaProvider.ProviderIdValue);
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
            }

            if (!visible)
            {
                _panel.HideNow();
            }

            _tray.UpdateVisibility(visible);
        }

        private void RecreateHost()
        {
            TaskbarStrip strip = new TaskbarStrip(BuildVisuals());
            strip.HoverChanged += OnStripHoverChanged;

            _strip = strip;
            _host = new TaskbarHost(strip);
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

            if (_scheduler != null)
            {
                _scheduler.ResultAvailable -= OnProviderResult;
                _scheduler.Dispose();
                _scheduler = null;
            }

            _quotaProvider = null;
            _quotaSnapshot = null;

            Theme.Changed -= OnThemeChanged;
            Theme.Shutdown();

            if (_panel != null)
            {
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
                _tray.SettingsRequested -= OnSettingsRequested;
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

            _host = null;
            _strip = null;

            Log.Write("VibeBar 退出");
            _app.Shutdown();
        }
    }
}
