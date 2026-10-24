using System;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace VibeBar
{
    /// <summary>
    /// 当前用户的开机自启注册表项。只操作 HKCU Run 下名为 VibeBar 的值，
    /// 不触碰其他程序的自启条目。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        internal const string ValueName = "VibeBar";

        internal static bool IsEnabled()
        {
            return IsEnabled(ValueName);
        }

        internal static bool IsEnabled(string valueName)
        {
            if (string.IsNullOrEmpty(valueName))
            {
                return false;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string value = key.GetValue(valueName) as string;
                    if (value == null)
                    {
                        return false;
                    }

                    string executable = Forms.Application.ExecutablePath;
                    return string.Equals(
                        value.Trim('"'),
                        executable,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception exception)
            {
                Log.Write("读取开机自启状态失败：" + exception.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 增删自启项。成功返回 null，失败返回中文错误并写日志。
        /// </summary>
        internal static string SetEnabled(bool enabled)
        {
            return SetEnabled(enabled, ValueName);
        }

        internal static string SetEnabled(bool enabled, string valueName)
        {
            if (string.IsNullOrEmpty(valueName))
            {
                return "开机自启值名无效";
            }

            try
            {
                using (RegistryKey key =
                    Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        return "无法打开开机自启注册表项";
                    }

                    if (enabled)
                    {
                        string executable = Forms.Application.ExecutablePath;
                        key.SetValue(
                            valueName,
                            "\"" + executable + "\"",
                            RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(valueName, false);
                    }
                }

                return null;
            }
            catch (Exception exception)
            {
                Log.Error("设置开机自启失败", exception);
                return "设置开机自启失败，详见日志";
            }
        }
    }
}
