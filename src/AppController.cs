using System;
using System.Windows;
using System.Windows.Threading;

namespace VibeBar
{
    /// <summary>
    /// 总装配：设置、任务栏宿主、详情面板、托盘与生命周期。
    /// T1 不包含任何 Provider、网络、Git 或事件总线。
    /// </summary>
    internal sealed class AppController
    {
        private readonly Application _app;
        private readonly SettingsStore _settingsStore;
        private readonly TaskbarWatcher _watcher;

        private AppSettings _settings;
        private TaskbarStrip _strip;
        private TaskbarHost _host;
        private DetailPanel _panel;
        private TrayController _tray;
        private bool _exiting;

        internal AppController(Application app)
        {
            if (app == null)
            {
                throw new ArgumentNullException("app");
            }

            _app = app;
            _settingsStore = new SettingsStore();
            _settings = _settingsStore.Load();

            Theme.Initialize(Dispatcher.CurrentDispatcher);
            Theme.Changed += OnThemeChanged;

            _watcher = new TaskbarWatcher();
            _watcher.TaskbarCreated += OnTaskbarCreated;

            CreateUi();

            Log.Write("VibeBar 启动，任务栏显示=" + _settings.General.ShowTaskbar);

            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(delegate
                {
                    if (_exiting)
                    {
                        return;
                    }

                    ApplyVisibility(_settings.General.ShowTaskbar);
                }));
        }

        private void CreateUi()
        {
            _strip = new TaskbarStrip(SegmentFactory.CreatePlaceholders());
            _strip.HoverChanged += OnStripHoverChanged;

            _panel = new DetailPanel();
            _panel.SetVisuals(_strip.GetVisuals());

            _host = new TaskbarHost(_strip);

            _tray = new TrayController();
            _tray.ToggleVisibilityRequested += OnToggleVisibilityRequested;
            _tray.ExitRequested += OnExitRequested;
            _tray.UpdateVisibility(_settings.General.ShowTaskbar);
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
            TaskbarStrip strip = new TaskbarStrip(SegmentFactory.CreatePlaceholders());
            strip.HoverChanged += OnStripHoverChanged;

            _strip = strip;
            _host = new TaskbarHost(strip);
            _panel.SetVisuals(strip.GetVisuals());
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
