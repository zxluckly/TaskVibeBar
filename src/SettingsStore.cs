using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VibeBar
{
    internal sealed class AppSettings
    {
        public int Version = 1;
        public GeneralSettings General = new GeneralSettings();
        public QuotaSettings Quota = new QuotaSettings();
        public GitSettings Git = new GitSettings();
        public AgentSettings Agent = new AgentSettings();
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

    internal sealed class UiSettings
    {
        public int MaxWidth = 420;
        public int MinWidth = 120;
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
        private readonly object _sync = new object();
        private readonly string _filePath;
        private Dictionary<string, object> _root;

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
                git["repoPath"] = settings.Git.RepoPath;
                git["includeUntracked"] = settings.Git.IncludeUntracked;
                git["pollSeconds"] = settings.Git.PollSeconds;

                Dictionary<string, object> agent = EnsureObject(_root, "agent");
                agent["enabled"] = settings.Agent.Enabled;
                agent["staleMinutes"] = settings.Agent.StaleMinutes;

                Dictionary<string, object> ui = EnsureObject(_root, "ui");
                ui["maxWidth"] = settings.Ui.MaxWidth;
                ui["minWidth"] = settings.Ui.MinWidth;

                Dictionary<string, object> hooks = EnsureObject(_root, "hooks");
                hooks["installed"] = settings.Hooks.Installed;
                hooks["statusLineInstalled"] = settings.Hooks.StatusLineInstalled;

                string text = Json.Serialize(_root);
                WriteAtomic(text);
            }
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
                settings.Git.PollSeconds = GetInt(git, "pollSeconds", 30);
            }

            Dictionary<string, object> agent = GetObject(root, "agent");
            if (agent != null)
            {
                settings.Agent.Enabled = GetBool(agent, "enabled", true);
                settings.Agent.StaleMinutes = GetInt(agent, "staleMinutes", 10);
            }

            Dictionary<string, object> ui = GetObject(root, "ui");
            if (ui != null)
            {
                settings.Ui.MaxWidth = GetInt(ui, "maxWidth", 420);
                settings.Ui.MinWidth = GetInt(ui, "minWidth", 120);
            }

            Dictionary<string, object> hooks = GetObject(root, "hooks");
            if (hooks != null)
            {
                settings.Hooks.Installed = GetBool(hooks, "installed", false);
                settings.Hooks.StatusLineInstalled =
                    GetBool(hooks, "statusLineInstalled", false);
            }
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
