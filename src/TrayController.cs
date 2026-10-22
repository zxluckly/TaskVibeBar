using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VibeBar
{
    internal sealed class TrayController : IDisposable
    {
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _visibilityItem;
        private readonly Icon _icon;
        private bool _disposed;

        internal event EventHandler ToggleVisibilityRequested;
        internal event EventHandler SettingsRequested;
        internal event EventHandler ExitRequested;

        internal TrayController()
        {
            _icon = CreateIcon();

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.ShowCheckMargin = true;

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置");
            settingsItem.Click += delegate
            {
                EventHandler handler = SettingsRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };

            _visibilityItem = new ToolStripMenuItem("显示/隐藏");
            _visibilityItem.CheckOnClick = false;
            _visibilityItem.Click += delegate
            {
                EventHandler handler = ToggleVisibilityRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate
            {
                EventHandler handler = ExitRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };

            _menu.Items.Add(settingsItem);
            _menu.Items.Add(_visibilityItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            _tray = new NotifyIcon();
            _tray.Text = "VibeBar · 任务栏状态带";
            _tray.Icon = _icon;
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
        }

        internal void UpdateVisibility(bool visible)
        {
            if (_visibilityItem != null)
            {
                _visibilityItem.Checked = visible;
            }
        }

        internal void ShowError(string message)
        {
            try
            {
                _tray.ShowBalloonTip(
                    3000,
                    "VibeBar",
                    message,
                    ToolTipIcon.Warning);
            }
            catch
            {
                // 气泡提示失败时只保留日志。
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            catch
            {
            }

            _menu.Dispose();
            _icon.Dispose();
        }

        private static Icon CreateIcon()
        {
            try
            {
                using (Bitmap bitmap = new Bitmap(32, 32))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.Clear(Color.Transparent);

                        RectangleF body = new RectangleF(2f, 2f, 28f, 28f);
                        using (GraphicsPath path = RoundedRect(body, 8f))
                        using (LinearGradientBrush brush = new LinearGradientBrush(
                            body,
                            Color.FromArgb(255, 78, 142, 247),
                            Color.FromArgb(255, 37, 99, 235),
                            90f))
                        {
                            graphics.FillPath(brush, path);
                        }

                        using (SolidBrush white = new SolidBrush(
                            Color.FromArgb(245, 255, 255, 255)))
                        {
                            using (GraphicsPath bar1 = RoundedRect(
                                new RectangleF(8f, 10f, 16f, 3f), 1.5f))
                            {
                                graphics.FillPath(white, bar1);
                            }

                            using (GraphicsPath bar2 = RoundedRect(
                                new RectangleF(8f, 15f, 11f, 3f), 1.5f))
                            {
                                graphics.FillPath(white, bar2);
                            }

                            using (GraphicsPath bar3 = RoundedRect(
                                new RectangleF(8f, 20f, 7f, 3f), 1.5f))
                            {
                                graphics.FillPath(white, bar3);
                            }
                        }
                    }

                    IntPtr handle = bitmap.GetHicon();
                    try
                    {
                        using (Icon temporary = Icon.FromHandle(handle))
                        {
                            return (Icon)temporary.Clone();
                        }
                    }
                    finally
                    {
                        NativeMethods.DestroyIcon(handle);
                    }
                }
            }
            catch
            {
                return (Icon)SystemIcons.Application.Clone();
            }
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            float diameter = Math.Max(1f, radius * 2f);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180f, 90f);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270f, 90f);
            path.AddArc(
                rect.Right - diameter,
                rect.Bottom - diameter,
                diameter,
                diameter,
                0f,
                90f);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }
}
