using System;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VibeBar
{
    internal static class Theme
    {
        private static readonly object Sync = new object();
        private static bool _cachedLight;
        private static DateTime _cachedAt = DateTime.MinValue;
        private static Dispatcher _dispatcher;
        private static bool _subscribed;

        internal static event EventHandler Changed;

        internal static void Initialize(Dispatcher dispatcher)
        {
            lock (Sync)
            {
                _dispatcher = dispatcher;
                if (_subscribed)
                {
                    return;
                }

                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _subscribed = true;
            }
        }

        internal static void Shutdown()
        {
            lock (Sync)
            {
                if (_subscribed)
                {
                    SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                    _subscribed = false;
                }

                _dispatcher = null;
            }
        }

        internal static bool IsLight()
        {
            if ((DateTime.UtcNow - _cachedAt).TotalSeconds < 1.0)
            {
                return _cachedLight;
            }

            _cachedLight = ReadSystemLightTheme();
            _cachedAt = DateTime.UtcNow;
            return _cachedLight;
        }

        internal static void Invalidate()
        {
            _cachedAt = DateTime.MinValue;
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Invalidate();

            Dispatcher dispatcher;
            lock (Sync)
            {
                dispatcher = _dispatcher;
            }

            if (dispatcher == null)
            {
                return;
            }

            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    EventHandler handler = Changed;
                    if (handler != null)
                    {
                        handler(null, EventArgs.Empty);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // 应用正在退出，消息循环已不可用。
            }
        }

        private static bool ReadSystemLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object system = key.GetValue("SystemUsesLightTheme");
                        if (system is int)
                        {
                            return (int)system != 0;
                        }

                        object apps = key.GetValue("AppsUseLightTheme");
                        if (apps is int)
                        {
                            return (int)apps != 0;
                        }
                    }
                }
            }
            catch
            {
                // 注册表读取失败时保持深色，避免启动失败。
            }

            return false;
        }
    }
}
