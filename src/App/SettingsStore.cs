using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace VibeBar
{
    internal sealed class AppSettings
    {
        public int Version = 1;
        public GeneralSettings General = new GeneralSettings();
        public QuotaSettings Quota = new QuotaSettings();
        public GitSettings Git = new GitSettings();
        public AgentSettings Agent = new AgentSettings();
        public PortsSettings Ports = new PortsSettings();
        public PrSettings Pr = new PrSettings();
        public CustomSettings Custom = new CustomSettings();
        public ExternalSettings External = new ExternalSettings();
        public UiSettings Ui = new UiSettings();
        public HookSettings Hooks = new HookSettings();
    }

    internal sealed class GeneralSettings
    {
        public bool ShowTaskbar = true;
        public string Theme = "system";
        public bool AutoStart = false;
        public bool PrivacyMode = false;
    }

    internal sealed class QuotaSettings
    {
        public string Provider = "deepseek";
        public string BaseUrl = "https://api.deepseek.com";
        public bool ReadKeyFromClaudeSettings = false;
        public int RefreshSeconds = 60;
        public decimal WarnBelow = 20m;
        public decimal CriticalBelow = 5m;
        public PeakValleySettings PeakValley = new PeakValleySettings();
    }

    internal sealed class PeakWindow
    {
        public string Start = "09:00";
        public string End = "12:00";
    }

    internal sealed class PeakValleySettings
    {
        public string TimeZone = "China Standard Time";
        public bool WeekendOffPeak = true;
        public PeakWindow[] PeakWindows = CreateDefaultPeakWindows();

        private static PeakWindow[] CreateDefaultPeakWindows()
        {
            PeakWindow morning = new PeakWindow();
            morning.Start = "09:00";
            morning.End = "12:00";

            PeakWindow afternoon = new PeakWindow();
            afternoon.Start = "14:00";
            afternoon.End = "18:00";

            return new PeakWindow[] { morning, afternoon };
        }
    }

    internal sealed class GitSettings
    {
        public string RepoPath = string.Empty;
        public bool IncludeUntracked = true;
        public int PollSeconds = 30;
    }

    internal sealed class AgentSettings
    {
        public bool Enabled = true;
        public int StaleMinutes = 10;
    }

    /// <summary>
    /// 端口段配置。WatchPorts 为空表示显示全部监听端口；
    /// 非空时只显示列表内的端口（常用前端/后端开发端口）。
    /// </summary>
    internal sealed class PortsSettings
    {
        public int PollSeconds = 5;
        public int[] WatchPorts = CreateDefaultWatchPorts();

        internal static int[] CreateDefaultWatchPorts()
        {
            return new int[] { 3000, 3001, 4200, 4321, 5000, 5173, 8000, 8080, 9000 };
        }
    }

    internal sealed class PrSettings
    {
        public int PollSeconds = 60;
    }

    /// <summary>
    /// 自定义命令（D4 / E1）。命令由用户本人编写，只在本地执行。
    /// Format 为 "text"（取首个非空行）或 "json"（{text,tooltip,color,url}）。
    /// </summary>
    internal sealed class CustomCommandSettings
    {
        public string Id = string.Empty;
        public string Label = string.Empty;
        public string Command = string.Empty;
        public int IntervalSeconds = 30;
        public string Format = "text";
        public string Url = string.Empty;
        public bool Enabled = true;
    }

    internal sealed class CustomSettings
    {
        public List<CustomCommandSettings> Commands = new List<CustomCommandSettings>();
    }

    /// <summary>
    /// 外部段（插件协议 v1）配置。外部段不出现在 ui.segments 里，
    /// 统一由 enabled 总开关控制；maxSegments 夹到 1..32，
    /// defaultTtlSeconds 夹到 5..3600。
    /// </summary>
    internal sealed class ExternalSettings
    {
        public bool Enabled = true;
        public int MaxSegments = 16;
        public int DefaultTtlSeconds = 30;

        internal static int NormalizeMaxSegments(int value)
        {
            if (value < 1)
            {
                return 1;
            }

            if (value > 32)
            {
                return 32;
            }

            return value;
        }

        internal static int NormalizeTtlSeconds(int value)
        {
            if (value < SegmentProtocol.MinTtlSeconds)
            {
                return SegmentProtocol.MinTtlSeconds;
            }

            if (value > SegmentProtocol.MaxTtlSeconds)
            {
                return SegmentProtocol.MaxTtlSeconds;
            }

            return value;
        }
    }

    internal sealed class SegmentSetting
    {
        public string Id;
        public bool Enabled = true;
        public int Priority;
    }

    internal sealed class UiSettings
    {
        public int MaxWidth = 420;
        public int MinWidth = 120;

        // 显示区域：吸附方向、水平偏移（px）。
        // Anchor = "right" 时偏移从托盘左缘算；"left" 时从任务栏左缘算。
        public string Anchor = "right";
        public int OffsetX = 8;

        // 显示器：primary（主屏任务栏）或 secondary（副屏任务栏）。
        public string Monitor = "primary";

        // 段的显隐/顺序。新增信息显示时由 AppController 的注册表补齐默认项。
        public List<SegmentSetting> Segments = new List<SegmentSetting>();

        // 悬停背景：空字符串表示跟随系统主题色；HoverOpacity 0-255。
        public string HoverColor = string.Empty;
        public int HoverOpacity = 235;

        internal bool IsSegmentEnabled(string id)
        {
            SegmentSetting setting = FindSegment(id);
            return setting == null || setting.Enabled;
        }

        internal int GetSegmentPriority(string id, int fallback)
        {
            SegmentSetting setting = FindSegment(id);
            if (setting == null || setting.Priority <= 0)
            {
                return fallback;
            }

            return setting.Priority;
        }

        internal void SetSegmentEnabled(string id, bool enabled)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            SegmentSetting setting = FindSegment(id);
            if (setting == null)
            {
                setting = new SegmentSetting();
                setting.Id = id;
                Segments.Add(setting);
            }

            setting.Enabled = enabled;
        }

        internal void EnsureSegments(IList<SegmentDescriptor> descriptors)
        {
            if (descriptors == null)
            {
                return;
            }

            bool anyEnabled = false;
            for (int index = 0; index < descriptors.Count; index++)
            {
                SegmentDescriptor descriptor = descriptors[index];
                if (descriptor == null || string.IsNullOrEmpty(descriptor.Id))
                {
                    continue;
                }

                SegmentSetting setting = FindSegment(descriptor.Id);
                if (setting == null)
                {
                    setting = new SegmentSetting();
                    setting.Id = descriptor.Id;
                    // 可选段（端口/PR）默认关闭，避免首次升级就多出一段噪音。
                    setting.Enabled = descriptor.DefaultEnabled;
                    setting.Priority = descriptor.DefaultPriority;
                    Segments.Add(setting);
                }
                else if (setting.Priority <= 0)
                {
                    setting.Priority = descriptor.DefaultPriority;
                }

                anyEnabled = anyEnabled || setting.Enabled;
            }

            // 防御：所有已知段都被关闭时，至少启用第一个。
            if (!anyEnabled && descriptors.Count > 0 && descriptors[0] != null)
            {
                SetSegmentEnabled(descriptors[0].Id, true);
            }
        }

        internal SegmentSetting FindSegment(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            for (int index = 0; index < Segments.Count; index++)
            {
                SegmentSetting setting = Segments[index];
                if (setting != null
                    && string.Equals(setting.Id, id, StringComparison.Ordinal))
                {
                    return setting;
                }
            }

            return null;
        }

        internal static void Normalize(UiSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            if (settings.MaxWidth < 120)
            {
                settings.MaxWidth = 120;
            }
            else if (settings.MaxWidth > 800)
            {
                settings.MaxWidth = 800;
            }

            if (settings.MinWidth < 60)
            {
                settings.MinWidth = 60;
            }

            if (settings.MinWidth > settings.MaxWidth)
            {
                settings.MinWidth = settings.MaxWidth;
            }

            if (string.Equals(
                settings.Anchor,
                "left",
                StringComparison.OrdinalIgnoreCase))
            {
                settings.Anchor = "left";
            }
            else
            {
                settings.Anchor = "right";
            }

            if (settings.OffsetX < 0)
            {
                settings.OffsetX = 0;
            }
            else if (settings.OffsetX > 400)
            {
                settings.OffsetX = 400;
            }

            if (string.Equals(
                settings.Monitor,
                "secondary",
                StringComparison.OrdinalIgnoreCase))
            {
                settings.Monitor = "secondary";
            }
            else
            {
                settings.Monitor = "primary";
            }

            if (settings.HoverOpacity < 0)
            {
                settings.HoverOpacity = 0;
            }
            else if (settings.HoverOpacity > 255)
            {
                settings.HoverOpacity = 255;
            }

            settings.HoverColor = NormalizeColor(settings.HoverColor);

            for (int index = settings.Segments.Count - 1; index >= 0; index--)
            {
                SegmentSetting setting = settings.Segments[index];
                if (setting == null || string.IsNullOrEmpty(setting.Id))
                {
                    settings.Segments.RemoveAt(index);
                    continue;
                }

                if (setting.Priority < 0)
                {
                    setting.Priority = 0;
                }
                else if (setting.Priority > 1000)
                {
                    setting.Priority = 1000;
                }

                // 去掉重复 id，保留第一个。
                for (int earlier = 0; earlier < index; earlier++)
                {
                    SegmentSetting other = settings.Segments[earlier];
                    if (other != null
                        && string.Equals(other.Id, setting.Id, StringComparison.Ordinal))
                    {
                        settings.Segments.RemoveAt(index);
                        break;
                    }
                }
            }
        }

        internal bool IsLeftAnchor
        {
            get
            {
                return string.Equals(
                    Anchor,
                    "left",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        internal bool IsSecondaryMonitor
        {
            get
            {
                return string.Equals(
                    Monitor,
                    "secondary",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static string NormalizeColor(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string text = value.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal))
            {
                text = "#" + text;
            }

            if (text.Length != 7)
            {
                return string.Empty;
            }

            for (int index = 1; index < text.Length; index++)
            {
                char c = text[index];
                bool hex = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return string.Empty;
                }
            }

            return text.ToUpperInvariant();
        }
    }

    internal sealed class HookSettings
    {
        public bool Installed = false;
        public bool StatusLineInstalled = false;
    }

    /// <summary>
    /// settings.json 的读写。内部保留原始字典，因此保存时不会丢失未知键。
    /// </summary>
    internal sealed class SettingsStore
    {
        // 防抖窗口与自身保存抑制窗口（毫秒）。文件事件只重置定时器，
        // 真正解析在定时器回调里做，连续保存只会应用最后一份。
        private const int DebounceMilliseconds = 300;
        private const int SelfSaveSuppressMilliseconds = 500;

        private readonly object _sync = new object();
        private readonly string _filePath;
        private Dictionary<string, object> _root;

        private FileSystemWatcher _fileWatcher;
        private Timer _debounceTimer;
        private bool _watching;
        private DateTime _suppressUntilUtc = DateTime.MinValue;
        private DateTime _lastHandledWriteUtc = DateTime.MinValue;

        /// <summary>
        /// 热重载完成时触发。回调在后台线程执行，参数是新解析出的设置对象；
        /// 订阅方自行切回 UI 线程。
        /// </summary>
        internal event Action<AppSettings> Reloaded;

        internal SettingsStore()
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VibeBar");
            _filePath = Path.Combine(folder, "settings.json");
            _root = new Dictionary<string, object>();
        }

        internal string FilePath
        {
            get { return _filePath; }
        }

        /// <summary>
        /// 开始监听 settings.json 的热重载。幂等：重复调用不会创建第二个 watcher。
        /// 监听的是目录而不是文件——Save 用 File.Replace 落盘，只监听文件会漏事件。
        /// 创建失败（无权限/目录不存在）只记日志，不影响启动。
        /// </summary>
        internal void StartWatching()
        {
            lock (_sync)
            {
                if (_watching)
                {
                    return;
                }

                _watching = true;
            }

            FileSystemWatcher watcher = null;
            Timer timer = null;
            try
            {
                string folder = Path.GetDirectoryName(_filePath);
                watcher = new FileSystemWatcher(folder, "settings.json");
                watcher.NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime
                    | NotifyFilters.FileName;
                watcher.Changed += OnWatchedFileChanged;
                watcher.Created += OnWatchedFileChanged;
                watcher.Renamed += OnWatchedFileRenamed;

                // 先建定时器再开监听，避免事件先于定时器到达。
                timer = new Timer(
                    OnDebounceElapsed,
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);

                lock (_sync)
                {
                    _fileWatcher = watcher;
                    _debounceTimer = timer;
                }

                watcher.EnableRaisingEvents = true;
                Log.Write("设置热重载已启用：" + folder);
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _fileWatcher = null;
                    _debounceTimer = null;
                    _watching = false;
                }

                DisposeQuietly(watcher, timer);
                Log.Write("设置热重载启用失败，已忽略：" + exception.GetType().Name);
            }
        }

        /// <summary>
        /// 停止监听并释放 watcher/timer。幂等：未启动或重复调用都安全。
        /// </summary>
        internal void StopWatching()
        {
            FileSystemWatcher watcher;
            Timer timer;
            lock (_sync)
            {
                watcher = _fileWatcher;
                timer = _debounceTimer;
                _fileWatcher = null;
                _debounceTimer = null;
                _watching = false;
            }

            if (watcher != null)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Changed -= OnWatchedFileChanged;
                    watcher.Created -= OnWatchedFileChanged;
                    watcher.Renamed -= OnWatchedFileRenamed;
                }
                catch (Exception exception)
                {
                    Log.Write("停止设置监听失败：" + exception.GetType().Name);
                }
            }

            DisposeQuietly(watcher, timer);
        }

        private static void DisposeQuietly(FileSystemWatcher watcher, Timer timer)
        {
            if (watcher != null)
            {
                try
                {
                    watcher.Dispose();
                }
                catch
                {
                    // 退出阶段重复释放忽略。
                }
            }

            if (timer != null)
            {
                try
                {
                    timer.Dispose();
                }
                catch
                {
                    // 退出阶段重复释放忽略。
                }
            }
        }

        private void OnWatchedFileChanged(object sender, FileSystemEventArgs e)
        {
            RestartDebounce();
        }

        private void OnWatchedFileRenamed(object sender, RenamedEventArgs e)
        {
            RestartDebounce();
        }

        private void RestartDebounce()
        {
            Timer timer;
            lock (_sync)
            {
                timer = _debounceTimer;
            }

            if (timer == null)
            {
                return;
            }

            try
            {
                timer.Change(DebounceMilliseconds, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // 退出过程中 watcher/timer 已被释放。
            }
        }

        private void OnDebounceElapsed(object state)
        {
            try
            {
                bool watching;
                lock (_sync)
                {
                    watching = _watching;
                }

                if (!watching)
                {
                    return;
                }

                DateTime lastWriteUtc = DateTime.MinValue;
                try
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(_filePath);
                }
                catch (Exception)
                {
                    lastWriteUtc = DateTime.MinValue;
                }

                if (!ShouldReload(lastWriteUtc))
                {
                    return;
                }

                ReloadFromDisk();
            }
            catch (Exception exception)
            {
                Log.Write("设置热重载失败：" + exception.GetType().Name);
            }
        }

        /// <summary>
        /// 判定本次文件事件是否应触发重载：自身保存抑制窗口内、或同一份写入
        /// （LastWriteTime 相同）已处理过，都跳过。
        /// </summary>
        internal bool ShouldReload(DateTime lastWriteUtc)
        {
            return ShouldReloadAt(DateTime.UtcNow, lastWriteUtc);
        }

        /// <summary>ShouldReload 的可测版本，nowUtc 由调用方给定。</summary>
        internal bool ShouldReloadAt(DateTime nowUtc, DateTime lastWriteUtc)
        {
            lock (_sync)
            {
                if (nowUtc < _suppressUntilUtc)
                {
                    return false;
                }

                if (lastWriteUtc != DateTime.MinValue)
                {
                    if (lastWriteUtc == _lastHandledWriteUtc)
                    {
                        return false;
                    }

                    _lastHandledWriteUtc = lastWriteUtc;
                }

                return true;
            }
        }

        /// <summary>
        /// Save() 落盘后调用：记录自身写入的时间戳并开启 500ms 抑制窗口，
        /// 避免 File.Replace 触发的监听事件把刚保存的配置再重载一遍。
        /// </summary>
        internal void MarkSelfSave()
        {
            // 任何异常都不能抛给 Save() 调用方（设置窗口会把它当成保存失败弹窗）；
            // 标记失败最坏结果只是多热重载一次。
            try
            {
                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(_filePath);
                lock (_sync)
                {
                    _suppressUntilUtc = DateTime.UtcNow.AddMilliseconds(
                        SelfSaveSuppressMilliseconds);
                    if (lastWriteUtc != DateTime.MinValue)
                    {
                        _lastHandledWriteUtc = lastWriteUtc;
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Debug("记录自身保存时间失败：" + exception.GetType().Name);
            }
        }

        private void ReloadFromDisk()
        {
            string text;
            try
            {
                if (!File.Exists(_filePath))
                {
                    Log.Write("设置解析失败，保留原配置：文件不存在");
                    return;
                }

                text = File.ReadAllText(_filePath, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                Log.Write("设置解析失败，保留原配置：" + exception.GetType().Name);
                return;
            }

            Dictionary<string, object> root;
            string error;
            AppSettings settings = ParseSettings(text, out root, out error);
            if (error != null)
            {
                Log.Write("设置解析失败，保留原配置：" + error);
                return;
            }

            lock (_sync)
            {
                _root = root;
            }

            // 这里只代表"文件解析成功"，是否真正生效由 AppController 决定后打日志。
            Log.Write("设置文件已重新解析，等待应用");

            // 不在锁内触发回调：UI 线程回调里可能再次 Save()。
            Action<AppSettings> handler = Reloaded;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(settings);
            }
            catch (Exception exception)
            {
                Log.Write("设置热重载回调失败：" + exception.GetType().Name);
            }
        }

        /// <summary>
        /// 解析 settings.json 文本。失败时不抛异常：返回默认值对象并由 error 给出原因，
        /// 调用方必须丢弃该对象、保留旧配置（绝不能把默认值应用上去）。
        /// </summary>
        internal static AppSettings ParseSettings(
            string text,
            out Dictionary<string, object> root,
            out string error)
        {
            root = null;
            error = null;

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                error = "空内容";
                return new AppSettings();
            }

            Dictionary<string, object> parsed;
            try
            {
                parsed = Json.Deserialize<Dictionary<string, object>>(text);
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name;
                return new AppSettings();
            }

            if (parsed == null)
            {
                error = "根对象无效";
                return new AppSettings();
            }

            AppSettings settings = new AppSettings();
            try
            {
                Apply(settings, parsed);
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name;
                return new AppSettings();
            }

            root = parsed;
            return settings;
        }

        internal AppSettings Load()
        {
            lock (_sync)
            {
                AppSettings settings = new AppSettings();
                _root = new Dictionary<string, object>();

                try
                {
                    if (!File.Exists(_filePath))
                    {
                        Log.Write("设置文件不存在，使用默认设置");
                        return settings;
                    }

                    string text = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Log.Write("设置文件为空，使用默认设置");
                        return settings;
                    }

                    Dictionary<string, object> root =
                        Json.Deserialize<Dictionary<string, object>>(text);
                    if (root == null)
                    {
                        Log.Write("设置文件根对象无效，使用默认设置");
                        return settings;
                    }

                    _root = root;
                    Apply(settings, root);
                }
                catch (Exception exception)
                {
                    Log.Error("读取设置失败，使用默认设置", exception);
                    _root = new Dictionary<string, object>();
                }

                return settings;
            }
        }

        internal void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            lock (_sync)
            {
                if (_root == null)
                {
                    _root = new Dictionary<string, object>();
                }

                _root["version"] = settings.Version;

                Dictionary<string, object> general = EnsureObject(_root, "general");
                general["showTaskbar"] = settings.General.ShowTaskbar;
                general["theme"] = settings.General.Theme;
                general["autoStart"] = settings.General.AutoStart;
                general["privacyMode"] = settings.General.PrivacyMode;

                Dictionary<string, object> quota = EnsureObject(_root, "quota");
                quota["provider"] = settings.Quota.Provider;
                quota["baseUrl"] = settings.Quota.BaseUrl;
                quota["readKeyFromClaudeSettings"] = settings.Quota.ReadKeyFromClaudeSettings;
                settings.Quota.RefreshSeconds =
                    NormalizeRefreshSeconds(settings.Quota.RefreshSeconds);
                quota["refreshSeconds"] = settings.Quota.RefreshSeconds;
                quota["warnBelow"] = settings.Quota.WarnBelow;
                quota["criticalBelow"] = settings.Quota.CriticalBelow;

                Dictionary<string, object> peakValley = EnsureObject(quota, "peakValley");
                peakValley["timeZone"] = settings.Quota.PeakValley.TimeZone;
                peakValley["weekendOffPeak"] = settings.Quota.PeakValley.WeekendOffPeak;
                peakValley["peakWindows"] = BuildPeakWindows(
                    peakValley,
                    settings.Quota.PeakValley.PeakWindows);
                // 旧版 schema 的键不再写出。
                peakValley.Remove("offPeakStart");
                peakValley.Remove("offPeakEnd");

                Dictionary<string, object> git = EnsureObject(_root, "git");
                settings.Git.PollSeconds =
                    NormalizeGitPollSeconds(settings.Git.PollSeconds);
                git["repoPath"] = settings.Git.RepoPath;
                git["includeUntracked"] = settings.Git.IncludeUntracked;
                git["pollSeconds"] = settings.Git.PollSeconds;

                Dictionary<string, object> agent = EnsureObject(_root, "agent");
                agent["enabled"] = settings.Agent.Enabled;
                agent["staleMinutes"] = settings.Agent.StaleMinutes;

                Dictionary<string, object> ports = EnsureObject(_root, "ports");
                settings.Ports.PollSeconds =
                    NormalizePortPollSeconds(settings.Ports.PollSeconds);
                ports["pollSeconds"] = settings.Ports.PollSeconds;
                ports["watchPorts"] = BuildIntArray(settings.Ports.WatchPorts);

                Dictionary<string, object> pr = EnsureObject(_root, "pr");
                settings.Pr.PollSeconds = NormalizePrPollSeconds(settings.Pr.PollSeconds);
                pr["pollSeconds"] = settings.Pr.PollSeconds;

                NormalizeCustomCommands(settings.Custom);
                Dictionary<string, object> custom = EnsureObject(_root, "custom");
                custom["commands"] = BuildCustomCommands(
                    custom,
                    settings.Custom.Commands);

                settings.External.MaxSegments =
                    ExternalSettings.NormalizeMaxSegments(settings.External.MaxSegments);
                settings.External.DefaultTtlSeconds =
                    ExternalSettings.NormalizeTtlSeconds(settings.External.DefaultTtlSeconds);
                Dictionary<string, object> external = EnsureObject(_root, "external");
                external["enabled"] = settings.External.Enabled;
                external["maxSegments"] = settings.External.MaxSegments;
                external["defaultTtlSeconds"] = settings.External.DefaultTtlSeconds;

                UiSettings.Normalize(settings.Ui);
                Dictionary<string, object> ui = EnsureObject(_root, "ui");
                ui["maxWidth"] = settings.Ui.MaxWidth;
                ui["minWidth"] = settings.Ui.MinWidth;
                ui["anchor"] = settings.Ui.Anchor;
                ui["offsetX"] = settings.Ui.OffsetX;
                ui["monitor"] = settings.Ui.Monitor;
                ui["segments"] = BuildSegments(settings.Ui.Segments);
                // 旧版三键不再写出。
                ui.Remove("showQuota");
                ui.Remove("showGit");
                ui.Remove("showAgent");
                ui["hoverColor"] = settings.Ui.HoverColor;
                ui["hoverOpacity"] = settings.Ui.HoverOpacity;

                Dictionary<string, object> hooks = EnsureObject(_root, "hooks");
                hooks["installed"] = settings.Hooks.Installed;
                hooks["statusLineInstalled"] = settings.Hooks.StatusLineInstalled;

                string text = Json.Serialize(_root);
                WriteAtomic(text);
            }

            // 自身保存抑制：写在锁外，且只影响监听事件，不改动配置内容。
            MarkSelfSave();
        }

        private static void Apply(AppSettings settings, Dictionary<string, object> root)
        {
            settings.Version = GetInt(root, "version", 1);

            Dictionary<string, object> general = GetObject(root, "general");
            if (general != null)
            {
                settings.General.ShowTaskbar = GetBool(general, "showTaskbar", true);
                settings.General.Theme = GetString(general, "theme", "system");
                settings.General.AutoStart = GetBool(general, "autoStart", false);
                settings.General.PrivacyMode = GetBool(general, "privacyMode", false);
            }

            Dictionary<string, object> quota = GetObject(root, "quota");
            if (quota != null)
            {
                settings.Quota.Provider = GetString(quota, "provider", "deepseek");
                settings.Quota.BaseUrl = GetString(quota, "baseUrl", "https://api.deepseek.com");
                settings.Quota.ReadKeyFromClaudeSettings =
                    GetBool(quota, "readKeyFromClaudeSettings", false);
                settings.Quota.RefreshSeconds =
                    NormalizeRefreshSeconds(GetInt(quota, "refreshSeconds", 60));
                settings.Quota.WarnBelow = GetDecimal(quota, "warnBelow", 20m);
                settings.Quota.CriticalBelow = GetDecimal(quota, "criticalBelow", 5m);

                Dictionary<string, object> peakValley = GetObject(quota, "peakValley");
                if (peakValley != null)
                {
                    settings.Quota.PeakValley.TimeZone =
                        GetString(peakValley, "timeZone", "China Standard Time");
                    settings.Quota.PeakValley.WeekendOffPeak =
                        GetBool(peakValley, "weekendOffPeak", true);

                    PeakWindow[] windows = GetPeakWindows(peakValley);
                    if (windows != null)
                    {
                        settings.Quota.PeakValley.PeakWindows = windows;
                    }
                }
            }

            Dictionary<string, object> git = GetObject(root, "git");
            if (git != null)
            {
                settings.Git.RepoPath = GetString(git, "repoPath", string.Empty);
                settings.Git.IncludeUntracked = GetBool(git, "includeUntracked", true);
                settings.Git.PollSeconds =
                    NormalizeGitPollSeconds(GetInt(git, "pollSeconds", 30));
            }

            Dictionary<string, object> agent = GetObject(root, "agent");
            if (agent != null)
            {
                settings.Agent.Enabled = GetBool(agent, "enabled", true);
                settings.Agent.StaleMinutes = GetInt(agent, "staleMinutes", 10);
            }

            Dictionary<string, object> ports = GetObject(root, "ports");
            if (ports != null)
            {
                settings.Ports.PollSeconds =
                    NormalizePortPollSeconds(GetInt(ports, "pollSeconds", 5));
                int[] watchPorts = GetIntArray(ports, "watchPorts");
                if (watchPorts != null)
                {
                    settings.Ports.WatchPorts = watchPorts;
                }
            }

            Dictionary<string, object> pr = GetObject(root, "pr");
            if (pr != null)
            {
                settings.Pr.PollSeconds =
                    NormalizePrPollSeconds(GetInt(pr, "pollSeconds", 60));
            }

            Dictionary<string, object> custom = GetObject(root, "custom");
            if (custom != null)
            {
                ApplyCustomCommands(settings.Custom, custom);
            }

            Dictionary<string, object> external = GetObject(root, "external");
            if (external != null)
            {
                settings.External.Enabled = GetBool(external, "enabled", true);
                settings.External.MaxSegments = ExternalSettings.NormalizeMaxSegments(
                    GetInt(external, "maxSegments", 16));
                settings.External.DefaultTtlSeconds =
                    ExternalSettings.NormalizeTtlSeconds(
                        GetInt(external, "defaultTtlSeconds", 30));
            }

            Dictionary<string, object> ui = GetObject(root, "ui");
            if (ui != null)
            {
                settings.Ui.MaxWidth = GetInt(ui, "maxWidth", 420);
                settings.Ui.MinWidth = GetInt(ui, "minWidth", 120);
                settings.Ui.Anchor = GetString(ui, "anchor", "right");
                settings.Ui.OffsetX = GetInt(ui, "offsetX", 8);
                settings.Ui.Monitor = GetString(ui, "monitor", "primary");
                ApplySegments(settings.Ui, ui);
                settings.Ui.HoverColor = GetString(ui, "hoverColor", string.Empty);
                settings.Ui.HoverOpacity = GetInt(ui, "hoverOpacity", 235);
            }

            UiSettings.Normalize(settings.Ui);

            Dictionary<string, object> hooks = GetObject(root, "hooks");
            if (hooks != null)
            {
                settings.Hooks.Installed = GetBool(hooks, "installed", false);
                settings.Hooks.StatusLineInstalled =
                    GetBool(hooks, "statusLineInstalled", false);
            }
        }

        private static object[] BuildSegments(List<SegmentSetting> segments)
        {
            if (segments == null)
            {
                return new object[0];
            }

            object[] result = new object[segments.Count];
            for (int index = 0; index < segments.Count; index++)
            {
                SegmentSetting setting = segments[index];
                Dictionary<string, object> item = new Dictionary<string, object>();
                if (setting != null)
                {
                    item["id"] = setting.Id;
                    item["enabled"] = setting.Enabled;
                    item["priority"] = setting.Priority;
                }

                result[index] = item;
            }

            return result;
        }

        private static void ApplySegments(
            UiSettings settings,
            Dictionary<string, object> ui)
        {
            object value;
            if (ui.TryGetValue("segments", out value) && value != null)
            {
                object[] items = value as object[];
                if (items != null)
                {
                    for (int index = 0; index < items.Length; index++)
                    {
                        Dictionary<string, object> item =
                            items[index] as Dictionary<string, object>;
                        if (item == null)
                        {
                            continue;
                        }

                        object idValue;
                        if (!item.TryGetValue("id", out idValue)
                            || !(idValue is string)
                            || string.IsNullOrEmpty((string)idValue))
                        {
                            continue;
                        }

                        SegmentSetting setting = new SegmentSetting();
                        setting.Id = (string)idValue;
                        setting.Enabled = GetBool(item, "enabled", true);
                        setting.Priority = GetInt(item, "priority", 0);
                        settings.Segments.Add(setting);
                    }
                }

                return;
            }

            // 兼容 T6 之前的 showQuota/showGit/showAgent。
            AddLegacySegment(settings, ui, "quota", "showQuota", 10);
            AddLegacySegment(settings, ui, "git", "showGit", 20);
            AddLegacySegment(settings, ui, "agent", "showAgent", 30);
        }

        private static void AddLegacySegment(
            UiSettings settings,
            Dictionary<string, object> ui,
            string id,
            string legacyKey,
            int priority)
        {
            SegmentSetting setting = new SegmentSetting();
            setting.Id = id;
            setting.Enabled = GetBool(ui, legacyKey, true);
            setting.Priority = priority;
            settings.Segments.Add(setting);
        }

        private static object[] BuildPeakWindows(
            Dictionary<string, object> peakValley,
            PeakWindow[] windows)
        {
            if (windows == null || windows.Length == 0)
            {
                PeakWindow morning = new PeakWindow();
                morning.Start = "09:00";
                morning.End = "12:00";

                PeakWindow afternoon = new PeakWindow();
                afternoon.Start = "14:00";
                afternoon.End = "18:00";

                windows = new PeakWindow[] { morning, afternoon };
            }

            IList existing = null;
            object existingValue;
            if (peakValley.TryGetValue("peakWindows", out existingValue))
            {
                existing = existingValue as IList;
            }

            object[] result = new object[windows.Length];
            for (int index = 0; index < windows.Length; index++)
            {
                PeakWindow window = windows[index];
                if (window == null)
                {
                    window = new PeakWindow();
                }

                // 同位置的旧数组元素若有未知键，读-改-写时保留。
                Dictionary<string, object> item = null;
                if (existing != null && index < existing.Count)
                {
                    item = existing[index] as Dictionary<string, object>;
                }

                if (item == null)
                {
                    item = new Dictionary<string, object>();
                }

                item["start"] = window.Start;
                item["end"] = window.End;
                result[index] = item;
            }

            return result;
        }

        private static PeakWindow[] GetPeakWindows(
            Dictionary<string, object> peakValley)
        {
            object value;
            if (!peakValley.TryGetValue("peakWindows", out value) || value == null)
            {
                return null;
            }

            IList items = value as IList;
            if (items == null || items.Count == 0)
            {
                Log.Write("设置项类型错误：peakWindows，已使用默认值");
                return null;
            }

            PeakWindow[] result = new PeakWindow[items.Count];
            for (int index = 0; index < items.Count; index++)
            {
                Dictionary<string, object> item =
                    items[index] as Dictionary<string, object>;
                if (item == null)
                {
                    Log.Write("设置项类型错误：peakWindows，已使用默认值");
                    return null;
                }

                object startValue;
                object endValue;
                if (!item.TryGetValue("start", out startValue)
                    || !item.TryGetValue("end", out endValue)
                    || !(startValue is string)
                    || !(endValue is string))
                {
                    Log.Write("设置项类型错误：peakWindows，已使用默认值");
                    return null;
                }

                PeakWindow window = new PeakWindow();
                window.Start = (string)startValue;
                window.End = (string)endValue;
                result[index] = window;
            }

            return result;
        }

        private static object[] BuildIntArray(int[] values)
        {
            if (values == null || values.Length == 0)
            {
                return new object[0];
            }

            object[] result = new object[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                result[index] = values[index];
            }

            return result;
        }

        /// <summary>
        /// 读取整数数组。键缺失返回 null（保留默认值），显式空数组返回空数组。
        /// </summary>
        private static int[] GetIntArray(Dictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            IList items = value as IList;
            if (items == null)
            {
                Log.Write("设置项类型错误：" + key + "，已使用默认值");
                return null;
            }

            List<int> result = new List<int>();
            for (int index = 0; index < items.Count; index++)
            {
                object item = items[index];
                int port = 0;
                bool parsed = false;
                if (item is int)
                {
                    port = (int)item;
                    parsed = true;
                }
                else if (item is long)
                {
                    port = (int)(long)item;
                    parsed = true;
                }
                else if (item is decimal)
                {
                    port = (int)(decimal)item;
                    parsed = true;
                }
                else if (item is double)
                {
                    port = (int)(double)item;
                    parsed = true;
                }

                if (!parsed || port <= 0 || port > 65535)
                {
                    Log.Write("设置项数值非法：" + key + "，已忽略该元素");
                    continue;
                }

                if (!result.Contains(port))
                {
                    result.Add(port);
                }
            }

            return result.ToArray();
        }

        private static int NormalizePortPollSeconds(int seconds)
        {
            if (seconds <= 0)
            {
                return 5;
            }

            if (seconds < 2)
            {
                return 2;
            }

            if (seconds > 600)
            {
                return 600;
            }

            return seconds;
        }

        private static int NormalizePrPollSeconds(int seconds)
        {
            if (seconds <= 0)
            {
                return 60;
            }

            if (seconds < 30)
            {
                return 30;
            }

            if (seconds > 3600)
            {
                return 3600;
            }

            return seconds;
        }

        /// <summary>
        /// 自定义命令的合法性收敛：命令为空则丢弃，id 缺失/重复自动补齐，
        /// 间隔与格式回退默认值，最多保留 8 条。
        /// </summary>
        internal static void NormalizeCustomCommands(CustomSettings settings)
        {
            if (settings == null || settings.Commands == null)
            {
                return;
            }

            List<CustomCommandSettings> valid = new List<CustomCommandSettings>();
            List<string> ids = new List<string>();
            for (int index = 0; index < settings.Commands.Count; index++)
            {
                CustomCommandSettings command = settings.Commands[index];
                if (command == null
                    || string.IsNullOrEmpty(command.Command)
                    || command.Command.Trim().Length == 0)
                {
                    continue;
                }

                if (valid.Count >= 8)
                {
                    Log.Write("自定义命令超过 8 条，已忽略多余配置");
                    break;
                }

                string id = command.Id == null ? string.Empty : command.Id.Trim();
                if (id.Length == 0)
                {
                    id = "cmd" + (index + 1).ToString();
                }

                string unique = id;
                int suffix = 2;
                while (ids.Contains(unique))
                {
                    unique = id + "-" + suffix.ToString();
                    suffix++;
                }

                command.Id = unique;
                ids.Add(unique);

                if (string.IsNullOrEmpty(command.Label)
                    || command.Label.Trim().Length == 0)
                {
                    command.Label = unique;
                }

                if (command.IntervalSeconds <= 0)
                {
                    command.IntervalSeconds = 30;
                }
                else if (command.IntervalSeconds < 10)
                {
                    command.IntervalSeconds = 10;
                }
                else if (command.IntervalSeconds > 3600)
                {
                    command.IntervalSeconds = 3600;
                }

                command.Format = string.Equals(
                    command.Format,
                    "json",
                    StringComparison.OrdinalIgnoreCase)
                    ? "json"
                    : "text";

                if (command.Url == null)
                {
                    command.Url = string.Empty;
                }

                valid.Add(command);
            }

            settings.Commands = valid;
        }

        private static object[] BuildCustomCommands(
            Dictionary<string, object> custom,
            List<CustomCommandSettings> commands)
        {
            if (commands == null || commands.Count == 0)
            {
                return new object[0];
            }

            IList existing = null;
            object value;
            if (custom.TryGetValue("commands", out value))
            {
                existing = value as IList;
            }

            object[] result = new object[commands.Count];
            for (int index = 0; index < commands.Count; index++)
            {
                CustomCommandSettings command = commands[index];
                Dictionary<string, object> item = null;
                if (existing != null && command != null)
                {
                    for (int existingIndex = 0; existingIndex < existing.Count; existingIndex++)
                    {
                        Dictionary<string, object> candidate =
                            existing[existingIndex] as Dictionary<string, object>;
                        if (candidate == null)
                        {
                            continue;
                        }

                        object idValue;
                        if (candidate.TryGetValue("id", out idValue)
                            && idValue is string
                            && string.Equals(
                                (string)idValue,
                                command.Id,
                                StringComparison.Ordinal))
                        {
                            item = candidate;
                            break;
                        }
                    }
                }

                if (item == null)
                {
                    item = new Dictionary<string, object>();
                }

                if (command != null)
                {
                    item["id"] = command.Id;
                    item["label"] = command.Label;
                    item["command"] = command.Command;
                    item["intervalSeconds"] = command.IntervalSeconds;
                    item["format"] = command.Format;
                    item["url"] = command.Url;
                    item["enabled"] = command.Enabled;
                }

                result[index] = item;
            }

            return result;
        }

        private static void ApplyCustomCommands(
            CustomSettings settings,
            Dictionary<string, object> custom)
        {
            object value;
            if (!custom.TryGetValue("commands", out value) || value == null)
            {
                return;
            }

            IList items = value as IList;
            if (items == null)
            {
                Log.Write("设置项类型错误：custom.commands，已使用默认值");
                return;
            }

            settings.Commands.Clear();
            for (int index = 0; index < items.Count; index++)
            {
                Dictionary<string, object> item =
                    items[index] as Dictionary<string, object>;
                if (item == null)
                {
                    continue;
                }

                CustomCommandSettings command = new CustomCommandSettings();
                command.Id = GetString(item, "id", string.Empty);
                command.Label = GetString(item, "label", string.Empty);
                command.Command = GetString(item, "command", string.Empty);
                command.IntervalSeconds = GetInt(item, "intervalSeconds", 30);
                command.Format = GetString(item, "format", "text");
                command.Url = GetString(item, "url", string.Empty);
                command.Enabled = GetBool(item, "enabled", true);
                settings.Commands.Add(command);
            }

            NormalizeCustomCommands(settings);
        }

        private static int NormalizeGitPollSeconds(int seconds)
        {
            if (seconds <= 0)
            {
                return 30;
            }

            if (seconds < 10)
            {
                Log.Write("设置项 git.pollSeconds 小于 10，已按 10 处理");
                return 10;
            }

            if (seconds > 86400)
            {
                Log.Write("设置项 git.pollSeconds 大于 86400，已按 86400 处理");
                return 86400;
            }

            return seconds;
        }

        private static int NormalizeRefreshSeconds(int seconds)
        {
            if (seconds < 60)
            {
                Log.Write("设置项 quota.refreshSeconds 小于 60，已按 60 处理");
                return 60;
            }

            if (seconds > 3600)
            {
                Log.Write("设置项 quota.refreshSeconds 大于 3600，已按 3600 处理");
                return 3600;
            }

            return seconds;
        }

        private void WriteAtomic(string text)
        {
            string folder = Path.GetDirectoryName(_filePath);
            Directory.CreateDirectory(folder);

            string temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));

            try
            {
                if (File.Exists(_filePath))
                {
                    File.Replace(temporary, _filePath, null);
                }
                else
                {
                    File.Move(temporary, _filePath);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static Dictionary<string, object> GetObject(
            Dictionary<string, object> parent,
            string key)
        {
            if (parent == null)
            {
                return null;
            }

            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary == null)
            {
                Log.Write("设置项类型错误：" + key + "，已使用默认值");
            }

            return dictionary;
        }

        private static Dictionary<string, object> EnsureObject(
            Dictionary<string, object> parent,
            string key)
        {
            object value;
            if (parent.TryGetValue(key, out value))
            {
                Dictionary<string, object> existing = value as Dictionary<string, object>;
                if (existing != null)
                {
                    return existing;
                }

                Log.Write("设置项类型错误：" + key + "，已替换为默认分组");
            }

            Dictionary<string, object> created = new Dictionary<string, object>();
            parent[key] = created;
            return created;
        }

        private static string GetString(
            Dictionary<string, object> parent,
            string key,
            string fallback)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            string text = value as string;
            if (text == null)
            {
                Log.Write("设置项类型错误：" + key + "，已使用默认值");
                return fallback;
            }

            return text;
        }

        private static bool GetBool(
            Dictionary<string, object> parent,
            string key,
            bool fallback)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            Log.Write("设置项类型错误：" + key + "，已使用默认值");
            return fallback;
        }

        private static int GetInt(
            Dictionary<string, object> parent,
            string key,
            int fallback)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            try
            {
                if (value is int)
                {
                    return (int)value;
                }

                if (value is long)
                {
                    return checked((int)(long)value);
                }

                if (value is decimal)
                {
                    return checked((int)(decimal)value);
                }

                if (value is double)
                {
                    return checked((int)(double)value);
                }
            }
            catch (OverflowException)
            {
                Log.Write("设置项数值越界：" + key + "，已使用默认值");
                return fallback;
            }

            Log.Write("设置项类型错误：" + key + "，已使用默认值");
            return fallback;
        }

        private static decimal GetDecimal(
            Dictionary<string, object> parent,
            string key,
            decimal fallback)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return fallback;
            }

            try
            {
                if (value is decimal)
                {
                    return (decimal)value;
                }

                if (value is int)
                {
                    return (int)value;
                }

                if (value is long)
                {
                    return (long)value;
                }

                if (value is double)
                {
                    return (decimal)(double)value;
                }
            }
            catch (OverflowException)
            {
                Log.Write("设置项数值越界：" + key + "，已使用默认值");
                return fallback;
            }

            Log.Write("设置项类型错误：" + key + "，已使用默认值");
            return fallback;
        }
    }
}
