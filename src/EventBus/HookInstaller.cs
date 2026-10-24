using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace VibeBar
{
    /// <summary>
    /// Claude Code Hooks 安装器。只修改目标 settings.json 的 hooks 字段：
    /// 安装前备份，幂等替换 VibeBar 条目，卸载时只移除 VibeBar 条目并清理
    /// 因此变空的事件数组。保留其他所有键、插件 hooks 与数组顺序。
    /// 不实现 statusLine（T5）。
    /// </summary>
    internal sealed class HookInstaller
    {
        private const string CliFileName = "VibeBar.Cli.exe";
        private const string HookArgument = "--hook";
        private const string StatusLineArgument = "--statusline";
        private const int HookTimeoutSeconds = 5;

        private static readonly string[] HookEvents = new string[]
        {
            "SessionStart",
            "UserPromptSubmit",
            "PreToolUse",
            "PostToolUse",
            "PermissionRequest",
            "Notification",
            "Stop",
            "SubagentStart",
            "SubagentStop",
            "SessionEnd"
        };

        private readonly string _settingsPath;
        private readonly string _cliPath;

        internal HookInstaller()
            : this(null, null)
        {
        }

        internal HookInstaller(string settingsPath)
            : this(settingsPath, null)
        {
        }

        /// <summary>
        /// settingsPath 为空时使用 %USERPROFILE%\.claude\settings.json；
        /// cliPath 为空时使用主程序目录下的 VibeBar.Cli.exe。
        /// </summary>
        internal HookInstaller(string settingsPath, string cliPath)
        {
            _settingsPath = string.IsNullOrEmpty(settingsPath)
                ? ResolveDefaultSettingsPath()
                : settingsPath;

            if (string.IsNullOrEmpty(cliPath))
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                _cliPath = Path.Combine(baseDirectory, CliFileName);
            }
            else
            {
                try
                {
                    _cliPath = Path.GetFullPath(cliPath);
                }
                catch (Exception)
                {
                    _cliPath = cliPath;
                }
            }
        }

        internal string SettingsPath
        {
            get { return _settingsPath; }
        }

        /// <summary>
        /// 检查目标文件中是否已有 VibeBar 的 Hook 条目。读取/解析失败返回 false。
        /// </summary>
        internal static string OriginalStatusLinePath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VibeBar");
                return Path.Combine(folder, "statusline-original.txt");
            }
        }

        internal bool IsStatusLineInstalled()
        {
            if (!CanUseSettingsPath() || !File.Exists(_settingsPath))
            {
                return false;
            }

            try
            {
                Dictionary<string, object> root = LoadRootQuiet();
                if (root == null)
                {
                    return false;
                }

                object value;
                if (!root.TryGetValue("statusLine", out value) || value == null)
                {
                    return false;
                }

                Dictionary<string, object> statusLine =
                    value as Dictionary<string, object>;
                if (statusLine == null)
                {
                    return false;
                }

                object commandValue;
                string command = statusLine.TryGetValue("command", out commandValue)
                    ? commandValue as string
                    : null;
                return command != null
                    && command.IndexOf(CliFileName, StringComparison.OrdinalIgnoreCase) >= 0
                    && command.IndexOf(StatusLineArgument, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception exception)
            {
                Log.Debug("检查 statusLine 安装状态失败：" + exception.GetType().Name);
                return false;
            }
        }

        private Dictionary<string, object> LoadRootQuiet()
        {
            try
            {
                string text = File.ReadAllText(_settingsPath, Encoding.UTF8);
                return Json.Deserialize<Dictionary<string, object>>(text);
            }
            catch
            {
                return null;
            }
        }

        internal bool IsInstalled()
        {
            if (!CanUseSettingsPath())
            {
                return false;
            }

            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return false;
                }

                string text = File.ReadAllText(_settingsPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                Dictionary<string, object> root =
                    Json.Deserialize<Dictionary<string, object>>(text);
                if (root == null)
                {
                    return false;
                }

                object hooksValue;
                if (!root.TryGetValue("hooks", out hooksValue) || hooksValue == null)
                {
                    return false;
                }

                Dictionary<string, object> hooks =
                    hooksValue as Dictionary<string, object>;
                if (hooks == null)
                {
                    return false;
                }

                foreach (KeyValuePair<string, object> pair in hooks)
                {
                    IList entries = pair.Value as IList;
                    if (entries == null)
                    {
                        continue;
                    }

                    for (int index = 0; index < entries.Count; index++)
                    {
                        if (EntryContainsVibeBarCli(entries[index]))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception exception)
            {
                Log.Debug("读取 Claude settings.json 失败：" + exception.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 安装/更新 Hooks。成功返回 null，失败返回中文错误（不写文件）。
        /// </summary>
        internal string Install()
        {
            if (!CanUseSettingsPath())
            {
                return "未找到用户目录，无法读写 settings.json";
            }

            string error;
            Dictionary<string, object> root = LoadRoot(out error);
            if (root == null)
            {
                return error;
            }

            Dictionary<string, object> hooks;
            if (!TryGetHooks(root, true, out hooks, out error))
            {
                return error;
            }

            // 幂等：先移除所有指向 VibeBar.Cli.exe 的条目，再插入。
            RemoveVibeBarEntries(hooks, false);

            for (int index = 0; index < HookEvents.Length; index++)
            {
                string eventName = HookEvents[index];
                IList entries;
                object value;
                if (hooks.TryGetValue(eventName, out value) && value != null)
                {
                    entries = value as IList;
                    if (entries == null)
                    {
                        return "settings.json 的 hooks."
                            + eventName
                            + " 结构异常，未做任何修改";
                    }
                }
                else
                {
                    entries = new ArrayList();
                    hooks[eventName] = entries;
                }

                entries.Add(CreateHookEntry());
            }

            error = InstallStatusLine(root);
            if (error != null)
            {
                return error;
            }

            string json;
            if (!TrySerializeValidated(root, out json, out error))
            {
                return error;
            }

            // 安装前备份原文件（若存在）。
            return WriteRoot(json, true);
        }

        /// <summary>
        /// 卸载 VibeBar Hooks。成功返回 null，失败返回中文错误。
        /// 不修改 statusLine 与其他任何配置。
        /// </summary>
        internal string Uninstall()
        {
            if (!CanUseSettingsPath())
            {
                return "未找到用户目录，无法读写 settings.json";
            }

            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            string error;
            Dictionary<string, object> root = LoadRoot(out error);
            if (root == null)
            {
                return error;
            }

            object hooksValue;
            if (!root.TryGetValue("hooks", out hooksValue) || hooksValue == null)
            {
                return null;
            }

            Dictionary<string, object> hooks =
                hooksValue as Dictionary<string, object>;
            if (hooks == null)
            {
                return "settings.json 的 hooks 不是对象，未做任何修改";
            }

            if (!ValidateHookLists(hooks, out error))
            {
                return error;
            }

            bool removedHooks = RemoveVibeBarEntries(hooks, true);
            bool removedStatusLine = UninstallStatusLine(root);
            if (!removedHooks && !removedStatusLine)
            {
                return null;
            }

            string json;
            if (!TrySerializeValidated(root, out json, out error))
            {
                return error;
            }

            return WriteRoot(json, false);
        }

        private Dictionary<string, object> LoadRoot(out string error)
        {
            error = null;

            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new Dictionary<string, object>();
                }

                string text = File.ReadAllText(_settingsPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    error = "settings.json 为空或内容不是有效 JSON，未做任何修改";
                    return null;
                }

                Dictionary<string, object> root =
                    Json.Deserialize<Dictionary<string, object>>(text);
                if (root == null)
                {
                    error = "settings.json 不是有效的 JSON 对象，未做任何修改";
                    return null;
                }

                return root;
            }
            catch (Exception exception)
            {
                Log.Write("读取 Claude settings.json 失败：" + exception.GetType().Name);
                error = "读取 settings.json 失败，未做任何修改";
                return null;
            }
        }

        private static bool TryGetHooks(
            Dictionary<string, object> root,
            bool createIfMissing,
            out Dictionary<string, object> hooks,
            out string error)
        {
            hooks = null;
            error = null;

            object value;
            if (root.TryGetValue("hooks", out value) && value != null)
            {
                hooks = value as Dictionary<string, object>;
                if (hooks == null)
                {
                    error = "settings.json 的 hooks 不是对象，未做任何修改";
                    return false;
                }
            }
            else
            {
                if (!createIfMissing)
                {
                    return true;
                }

                hooks = new Dictionary<string, object>();
                root["hooks"] = hooks;
            }

            return ValidateHookLists(hooks, out error);
        }

        private static bool ValidateHookLists(
            Dictionary<string, object> hooks,
            out string error)
        {
            error = null;
            foreach (KeyValuePair<string, object> pair in hooks)
            {
                if (pair.Value != null && !(pair.Value is IList))
                {
                    error = "settings.json 的 hooks."
                        + pair.Key
                        + " 不是数组，未做任何修改";
                    return false;
                }
            }

            return true;
        }

        private static bool RemoveVibeBarEntries(
            Dictionary<string, object> hooks,
            bool cleanupEmptyLists)
        {
            bool removedAny = false;
            List<string> keys = new List<string>();
            foreach (string key in hooks.Keys)
            {
                keys.Add(key);
            }

            for (int keyIndex = 0; keyIndex < keys.Count; keyIndex++)
            {
                string key = keys[keyIndex];
                IList entries = hooks[key] as IList;
                if (entries == null)
                {
                    continue;
                }

                bool removedHere = false;
                for (int index = entries.Count - 1; index >= 0; index--)
                {
                    if (EntryContainsVibeBarCli(entries[index]))
                    {
                        entries.RemoveAt(index);
                        removedHere = true;
                        removedAny = true;
                    }
                }

                if (cleanupEmptyLists && removedHere && entries.Count == 0)
                {
                    hooks.Remove(key);
                }
            }

            return removedAny;
        }

        private static bool EntryContainsVibeBarCli(object entry)
        {
            Dictionary<string, object> matcherEntry =
                entry as Dictionary<string, object>;
            if (matcherEntry == null)
            {
                return false;
            }

            object hooksValue;
            if (!matcherEntry.TryGetValue("hooks", out hooksValue) || hooksValue == null)
            {
                return false;
            }

            IList hooks = hooksValue as IList;
            if (hooks == null)
            {
                return false;
            }

            for (int index = 0; index < hooks.Count; index++)
            {
                Dictionary<string, object> hook =
                    hooks[index] as Dictionary<string, object>;
                if (hook == null)
                {
                    continue;
                }

                object commandValue;
                if (!hook.TryGetValue("command", out commandValue)
                    || commandValue == null)
                {
                    continue;
                }

                string command = commandValue as string;
                if (command != null
                    && command.IndexOf(
                        CliFileName,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private Dictionary<string, object> CreateHookEntry()
        {
            Dictionary<string, object> command = new Dictionary<string, object>();
            command["type"] = "command";
            // Claude Code 把 command 当作可执行文件名解析（不经过 shell），
            // 不能带引号；本路径无空格，中文路径由 Unicode API 处理。
            command["command"] = _cliPath;
            command["args"] = new object[] { HookArgument };
            command["timeout"] = HookTimeoutSeconds;

            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["matcher"] = string.Empty;
            entry["hooks"] = new object[] { command };
            return entry;
        }

        private string InstallStatusLine(Dictionary<string, object> root)
        {
            string existingCommand = null;
            object value;
            if (root.TryGetValue("statusLine", out value) && value != null)
            {
                Dictionary<string, object> existing =
                    value as Dictionary<string, object>;
                if (existing != null)
                {
                    object commandValue;
                    existingCommand = existing.TryGetValue("command", out commandValue)
                        ? commandValue as string
                        : null;
                }
            }

            bool existingIsOurs = existingCommand != null
                && existingCommand.IndexOf(
                    CliFileName,
                    StringComparison.OrdinalIgnoreCase) >= 0;

            if (!existingIsOurs && !string.IsNullOrEmpty(existingCommand))
            {
                try
                {
                    string folder = Path.GetDirectoryName(OriginalStatusLinePath);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    File.WriteAllText(
                        OriginalStatusLinePath,
                        existingCommand,
                        new UTF8Encoding(false));
                }
                catch (Exception exception)
                {
                    Log.Write("保存原 statusLine 失败：" + exception.GetType().Name);
                    return "保存原 statusLine 失败，未做任何修改";
                }
            }

            root["statusLine"] = CreateStatusLineEntry();
            return null;
        }

        private bool UninstallStatusLine(Dictionary<string, object> root)
        {
            object value;
            if (!root.TryGetValue("statusLine", out value) || value == null)
            {
                return false;
            }

            Dictionary<string, object> existing =
                value as Dictionary<string, object>;
            object commandValue;
            string command = existing == null
                ? null
                : (existing.TryGetValue("command", out commandValue)
                    ? commandValue as string
                    : null);

            if (command == null
                || command.IndexOf(CliFileName, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string original = ReadOriginalStatusLine();
            if (!string.IsNullOrEmpty(original))
            {
                Dictionary<string, object> restored =
                    new Dictionary<string, object>();
                restored["type"] = "command";
                restored["command"] = original;
                root["statusLine"] = restored;
                TryDelete(OriginalStatusLinePath);
            }
            else
            {
                root.Remove("statusLine");
                TryDelete(OriginalStatusLinePath);
            }

            return true;
        }

        private Dictionary<string, object> CreateStatusLineEntry()
        {
            string command = _cliPath.IndexOf(' ') >= 0
                ? "\"" + _cliPath + "\" " + StatusLineArgument
                : _cliPath + " " + StatusLineArgument;

            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["type"] = "command";
            entry["command"] = command;
            // 即使 Claude Code 不主动刷新，也每 5 秒重跑一次，保证上下文及时更新。
            entry["refreshInterval"] = 5;
            return entry;
        }

        private static string ReadOriginalStatusLine()
        {
            try
            {
                if (!File.Exists(OriginalStatusLinePath))
                {
                    return null;
                }

                string text = File.ReadAllText(OriginalStatusLinePath, Encoding.UTF8);
                return string.IsNullOrEmpty(text) ? null : text.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static bool TrySerializeValidated(
            Dictionary<string, object> root,
            out string json,
            out string error)
        {
            json = null;
            error = null;

            try
            {
                string serialized = Json.Serialize(root);
                if (string.IsNullOrEmpty(serialized))
                {
                    error = "生成的 settings.json 为空，未做任何修改";
                    return false;
                }

                // 写入前用同一序列化器反序列化校验。
                Dictionary<string, object> check =
                    Json.Deserialize<Dictionary<string, object>>(serialized);
                if (check == null)
                {
                    error = "生成的 settings.json 校验失败，未做任何修改";
                    return false;
                }

                json = serialized;
                return true;
            }
            catch (Exception exception)
            {
                Log.Write("校验 Claude settings.json 失败：" + exception.GetType().Name);
                error = "settings.json 校验失败，未做任何修改";
                return false;
            }
        }

        private string WriteRoot(string json, bool createBackup)
        {
            try
            {
                string directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string backupPath = null;
                if (createBackup && File.Exists(_settingsPath))
                {
                    backupPath = BuildBackupPath();
                    File.Copy(_settingsPath, backupPath, true);
                }

                string temporaryPath = _settingsPath + ".vibebar.tmp";

                try
                {
                    File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));

                    if (File.Exists(_settingsPath))
                    {
                        File.Replace(temporaryPath, _settingsPath, null);
                    }
                    else
                    {
                        File.Move(temporaryPath, _settingsPath);
                    }
                }
                catch (Exception exception)
                {
                    TryDelete(temporaryPath);

                    if (backupPath != null && File.Exists(backupPath))
                    {
                        try
                        {
                            File.Copy(backupPath, _settingsPath, true);
                        }
                        catch
                        {
                            // 回滚失败也要返回中文错误，不能抛出到 UI。
                        }
                    }

                    Log.Write("写入 Claude settings.json 失败：" + exception.GetType().Name);
                    return "写入 settings.json 失败，已尝试恢复备份";
                }

                return null;
            }
            catch (Exception exception)
            {
                Log.Write("写入 Claude settings.json 失败：" + exception.GetType().Name);
                return "写入 settings.json 失败，未做任何修改";
            }
        }

        private string BuildBackupPath()
        {
            string directory = Path.GetDirectoryName(_settingsPath);
            string fileName = Path.GetFileName(_settingsPath);
            string timestamp = DateTime.Now.ToString(
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture);
            string backupName = fileName + ".vibebar." + timestamp + ".bak";

            if (string.IsNullOrEmpty(directory))
            {
                return backupName;
            }

            return Path.Combine(directory, backupName);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private bool CanUseSettingsPath()
        {
            return !string.IsNullOrEmpty(_settingsPath);
        }

        private static string ResolveDefaultSettingsPath()
        {
            string profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
            {
                profile = Environment.GetEnvironmentVariable("USERPROFILE");
            }

            if (string.IsNullOrEmpty(profile))
            {
                return null;
            }

            return Path.Combine(
                Path.Combine(profile, ".claude"),
                "settings.json");
        }
    }
}
