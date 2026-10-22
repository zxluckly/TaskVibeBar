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
        private readonly ToolStripMenuItem _privacyItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly Icon _icon;
        private bool _disposed;

        internal event EventHandler ToggleVisibilityRequested;
        internal event EventHandler RefreshAllRequested;
        internal event EventHandler PrivacyModeRequested;
        internal event EventHandler AutoStartRequested;
        internal event EventHandler SettingsRequested;
        internal event EventHandler OpenDataFolderRequested;
        internal event EventHandler MenuOpening;
        internal event EventHandler ExitRequested;

        internal TrayController()
        {
            _icon = CreateIcon();

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.ShowCheckMargin = true;
            _menu.DropShadowEnabled = false;
            _menu.Font = new Font("Segoe UI", 9f);
            _menu.Padding = new Padding(4);
            _menu.Opening += delegate
            {
                ApplyMenuTheme();

                EventHandler handler = MenuOpening;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };

            _visibilityItem = CreateItem("显示/隐藏");
            _visibilityItem.Click += delegate { Raise(ToggleVisibilityRequested); };

            ToolStripMenuItem refreshAllItem = CreateItem("刷新全部");
            refreshAllItem.Click += delegate { Raise(RefreshAllRequested); };

            _privacyItem = CreateItem("隐私模式");
            _privacyItem.Click += delegate { Raise(PrivacyModeRequested); };

            _autoStartItem = CreateItem("开机自启");
            _autoStartItem.Click += delegate { Raise(AutoStartRequested); };

            ToolStripMenuItem settingsItem = CreateItem("设置");
            settingsItem.Click += delegate { Raise(SettingsRequested); };

            ToolStripMenuItem dataFolderItem = CreateItem("打开数据目录");
            dataFolderItem.Click += delegate { Raise(OpenDataFolderRequested); };

            ToolStripMenuItem exitItem = CreateItem("退出");
            exitItem.Click += delegate { Raise(ExitRequested); };

            _menu.Items.Add(_visibilityItem);
            _menu.Items.Add(refreshAllItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_privacyItem);
            _menu.Items.Add(_autoStartItem);
            _menu.Items.Add(settingsItem);
            _menu.Items.Add(dataFolderItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);

            ApplyMenuTheme();

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

        internal void UpdatePrivacy(bool privacy)
        {
            if (_privacyItem != null)
            {
                _privacyItem.Checked = privacy;
            }
        }

        internal void UpdateAutoStart(bool enabled)
        {
            if (_autoStartItem != null)
            {
                _autoStartItem.Checked = enabled;
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

        private void ApplyMenuTheme()
        {
            _menu.Renderer = new VibeMenuRenderer();
            _menu.BackColor = TrayTheme.ToDrawingColor(UiTheme.MenuBackground);
            _menu.ForeColor = TrayTheme.ToDrawingColor(UiTheme.TextPrimary);
        }

        private static ToolStripMenuItem CreateItem(string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.CheckOnClick = false;
            return item;
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
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

    /// <summary>
    /// 托盘菜单的深/浅色渲染。颜色每次绘制时从 UiTheme 读取，主题切换后无需重建菜单。
    /// </summary>
    internal sealed class VibeMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuBackground); }
        }

        public override Color ImageMarginGradientBegin
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuBackground); }
        }

        public override Color ImageMarginGradientMiddle
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuBackground); }
        }

        public override Color ImageMarginGradientEnd
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuBackground); }
        }

        public override Color MenuBorder
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuBorder); }
        }

        public override Color MenuItemBorder
        {
            get { return Color.Transparent; }
        }

        public override Color MenuItemSelected
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuHover); }
        }

        public override Color MenuItemSelectedGradientBegin
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuHover); }
        }

        public override Color MenuItemSelectedGradientEnd
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuHover); }
        }

        public override Color MenuItemPressedGradientBegin
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuHover); }
        }

        public override Color MenuItemPressedGradientEnd
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuHover); }
        }

        public override Color SeparatorDark
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuBorder); }
        }

        public override Color SeparatorLight
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuBorder); }
        }

        public override Color CheckBackground
        {
            get { return Color.Transparent; }
        }

        public override Color CheckSelectedBackground
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuHover); }
        }

        public override Color CheckPressedBackground
        {
            get { return TrayTheme.ToDrawingColor(UiTheme.MenuHover); }
        }
    }

    internal sealed class VibeMenuRenderer : ToolStripProfessionalRenderer
    {
        internal VibeMenuRenderer()
            : base(new VibeMenuColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(
            ToolStripRenderEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(TrayTheme.ToDrawingColor(UiTheme.MenuBackground)))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(
            ToolStripRenderEventArgs e)
        {
            using (Pen pen = new Pen(TrayTheme.ToDrawingColor(UiTheme.MenuBorder), 1f))
            {
                e.Graphics.DrawRectangle(
                    pen,
                    0,
                    0,
                    Math.Max(0, e.ToolStrip.Width - 1),
                    Math.Max(0, e.ToolStrip.Height - 1));
            }
        }

        protected override void OnRenderMenuItemBackground(
            ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }

            Rectangle bounds = new Rectangle(
                4,
                1,
                Math.Max(0, e.Item.Width - 8),
                Math.Max(0, e.Item.Height - 2));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = UiThemeRoundedRect(bounds, 4))
            using (SolidBrush brush = new SolidBrush(TrayTheme.ToDrawingColor(UiTheme.MenuHover)))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemText(
            ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? TrayTheme.ToDrawingColor(UiTheme.TextPrimary)
                : TrayTheme.ToDrawingColor(UiTheme.TextDisabled);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(
            ToolStripItemImageRenderEventArgs e)
        {
            Rectangle rect = e.ImageRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                rect = new Rectangle(6, (e.Item.Height - 10) / 2, 10, 10);
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(TrayTheme.ToDrawingColor(UiTheme.RoleColor(ColorRole.Success)), 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                Point[] points = new Point[]
                {
                    new Point(rect.Left + 1, rect.Top + rect.Height / 2),
                    new Point(rect.Left + rect.Width / 2 - 1, rect.Bottom - 2),
                    new Point(rect.Right - 1, rect.Top + 1)
                };
                e.Graphics.DrawLines(pen, points);
            }
        }

        protected override void OnRenderSeparator(
            ToolStripSeparatorRenderEventArgs e)
        {
            Rectangle rect = e.Item.ContentRectangle;
            using (Pen pen = new Pen(TrayTheme.ToDrawingColor(UiTheme.MenuBorder), 1f))
            {
                e.Graphics.DrawLine(
                    pen,
                    rect.Left + 10,
                    rect.Top + rect.Height / 2,
                    rect.Right - 10,
                    rect.Top + rect.Height / 2);
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = TrayTheme.ToDrawingColor(UiTheme.TextPrimary);
            base.OnRenderArrow(e);
        }

        private static GraphicsPath UiThemeRoundedRect(Rectangle rect, int radius)
        {
            int diameter = Math.Max(1, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(
                rect.Right - diameter,
                rect.Bottom - diameter,
                diameter,
                diameter,
                0,
                90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>
    /// UiTheme 使用 WPF 颜色；WinForms 菜单绘制需要 System.Drawing.Color。
    /// </summary>
    internal static class TrayTheme
    {
        internal static Color ToDrawingColor(System.Windows.Media.Color color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
