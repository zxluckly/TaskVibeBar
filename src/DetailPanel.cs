using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VibeBar
{
    /// <summary>
    /// 独立顶层、无激活、置顶的详情面板。绝不能 SetParent 到任务栏，
    /// 否则会被任务栏客户区裁剪。
    /// </summary>
    internal sealed class DetailPanel : Window
    {
        private readonly Border _root;
        private readonly TextBlock _heading;
        private readonly StackPanel _rows;
        private readonly List<TextBlock> _lineLabels = new List<TextBlock>();
        private readonly List<TextBlock> _titleLabels = new List<TextBlock>();
        private readonly DispatcherTimer _showTimer;
        private readonly DispatcherTimer _hideTimer;
        private readonly DispatcherTimer _visibilityTimer;

        private DetailSection[] _sections = new DetailSection[0];
        private Drawing.Rectangle _anchor = Drawing.Rectangle.Empty;
        private bool _panelVisible;
        private bool _disposed;

        internal DetailPanel()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = UiTheme.PanelWidth;
            MaxHeight = UiTheme.PanelMaxHeight;
            Opacity = 0;

            _root = new Border();
            _root.CornerRadius = new CornerRadius(10);
            _root.BorderThickness = new Thickness(1);
            _root.Padding = new Thickness(16);

            StackPanel stack = new StackPanel();

            _heading = new TextBlock();
            _heading.Text = "VibeBar";
            _heading.FontFamily = UiTheme.UiFont;
            _heading.FontSize = 14;
            _heading.FontWeight = FontWeights.SemiBold;
            stack.Children.Add(_heading);

            _rows = new StackPanel();
            _rows.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(_rows);

            _root.Child = stack;
            Content = _root;

            MouseEnter += delegate { CancelHide(); };
            MouseLeave += delegate { ScheduleHide(); };

            _showTimer = new DispatcherTimer();
            _showTimer.Interval = TimeSpan.FromMilliseconds(150);
            _showTimer.Tick += delegate
            {
                _showTimer.Stop();
                if (_disposed || _panelVisible)
                {
                    return;
                }

                IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (!IsTaskbarEffectivelyVisible(taskbar))
                {
                    return;
                }

                ShowPanel(taskbar);
            };

            _hideTimer = new DispatcherTimer();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(300);
            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                HideNow();
            };

            _visibilityTimer = new DispatcherTimer();
            _visibilityTimer.Interval = TimeSpan.FromMilliseconds(500);
            _visibilityTimer.Tick += delegate
            {
                if (!_panelVisible)
                {
                    _visibilityTimer.Stop();
                    return;
                }

                IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (!IsTaskbarEffectivelyVisible(taskbar))
                {
                    HideNow();
                }
            };

            ApplyTheme();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(
                handle,
                NativeMethods.GWL_EXSTYLE,
                style | NativeMethods.WS_EX_NOACTIVATE);
        }

        internal void SetSections(DetailSection[] sections)
        {
            _sections = sections == null ? new DetailSection[0] : sections;
            RebuildRows();
        }

        internal void ScheduleShow(Drawing.Rectangle anchor)
        {
            if (_disposed)
            {
                return;
            }

            _anchor = anchor;
            _hideTimer.Stop();
            if (_panelVisible)
            {
                return;
            }

            _showTimer.Stop();
            _showTimer.Start();
        }

        internal void ScheduleHide()
        {
            if (_disposed)
            {
                return;
            }

            _showTimer.Stop();
            if (!_panelVisible)
            {
                return;
            }

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        internal void HideNow()
        {
            _showTimer.Stop();
            _hideTimer.Stop();
            _visibilityTimer.Stop();
            _panelVisible = false;

            if (_disposed)
            {
                return;
            }

            Opacity = 0;
            Hide();
        }

        internal void ApplyTheme()
        {
            _root.Background = new SolidColorBrush(UiTheme.PanelBackground);
            _root.BorderBrush = new SolidColorBrush(UiTheme.Border);
            _heading.Foreground = new SolidColorBrush(UiTheme.TextPrimary);

            for (int index = 0; index < _titleLabels.Count; index++)
            {
                _titleLabels[index].Foreground =
                    new SolidColorBrush(UiTheme.TextPrimary);
            }

            for (int index = 0; index < _lineLabels.Count; index++)
            {
                _lineLabels[index].Foreground =
                    new SolidColorBrush(UiTheme.TextSecondary);
            }
        }

        internal void DisposePanel()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _showTimer.Stop();
            _hideTimer.Stop();
            _visibilityTimer.Stop();
            _panelVisible = false;

            try
            {
                Close();
            }
            catch
            {
                // 退出阶段窗口可能已被销毁。
            }
        }

        private void CancelHide()
        {
            _hideTimer.Stop();
        }

        private void ShowPanel(IntPtr taskbar)
        {
            if (!IsVisible)
            {
                Opacity = 0;
                Show();
            }

            UpdateLayout();
            PositionPanel(taskbar);

            _panelVisible = true;
            _visibilityTimer.Start();
            Opacity = 1;
        }

        private void PositionPanel(IntPtr taskbar)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            double actualWidth = ActualWidth;
            double actualHeight = ActualHeight;
            if (double.IsNaN(actualWidth) || double.IsNaN(actualHeight)
                || actualWidth <= 0 || actualHeight <= 0)
            {
                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
            double scaleY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
            int width = (int)Math.Ceiling(actualWidth * scaleX);
            int height = (int)Math.Ceiling(actualHeight * scaleY);

            Drawing.Rectangle work;
            try
            {
                work = Forms.Screen.FromHandle(taskbar).WorkingArea;
            }
            catch
            {
                work = Forms.Screen.PrimaryScreen.WorkingArea;
            }

            Drawing.Rectangle anchor = _anchor;
            if (anchor.Width <= 0 || anchor.Height <= 0)
            {
                NativeMethods.RECT taskbarRect;
                if (NativeMethods.GetWindowRect(taskbar, out taskbarRect))
                {
                    anchor = Drawing.Rectangle.FromLTRB(
                        taskbarRect.Left,
                        taskbarRect.Top,
                        taskbarRect.Right,
                        taskbarRect.Bottom);
                }
                else
                {
                    anchor = work;
                }
            }

            int x = anchor.Right - width;
            int y = anchor.Top - height - 8;

            // 任务栏在屏幕顶部或上方空间不足时改为向下弹出。
            if (y < work.Top + 8)
            {
                y = anchor.Bottom + 8;
            }

            int maxX = work.Right - width - 8;
            if (x > maxX)
            {
                x = maxX;
            }

            if (x < work.Left + 8)
            {
                x = work.Left + 8;
            }

            int maxY = work.Bottom - height - 8;
            if (y > maxY)
            {
                y = maxY;
            }

            if (y < work.Top + 8)
            {
                y = work.Top + 8;
            }

            NativeMethods.SetWindowPos(
                handle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                width,
                height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private static bool IsTaskbarEffectivelyVisible(IntPtr taskbar)
        {
            if (taskbar == IntPtr.Zero
                || !NativeMethods.IsWindow(taskbar)
                || !NativeMethods.IsWindowVisible(taskbar))
            {
                return false;
            }

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(taskbar, out rect))
            {
                return false;
            }

            Drawing.Rectangle bounds;
            try
            {
                bounds = Forms.Screen.FromHandle(taskbar).Bounds;
            }
            catch
            {
                return false;
            }

            Drawing.Rectangle windowRect = Drawing.Rectangle.FromLTRB(
                rect.Left,
                rect.Top,
                rect.Right,
                rect.Bottom);
            Drawing.Rectangle intersection = Drawing.Rectangle.Intersect(bounds, windowRect);
            // 任务栏自动隐藏时窗口通常只留下 2px 边缘，视为不可见。
            return intersection.Width > 4 && intersection.Height > 4;
        }

        private void RebuildRows()
        {
            _rows.Children.Clear();
            _lineLabels.Clear();
            _titleLabels.Clear();

            if (_sections.Length == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = "暂无数据";
                empty.FontFamily = UiTheme.UiFont;
                empty.FontSize = 13;
                empty.Margin = new Thickness(0, 2, 0, 2);
                empty.TextTrimming = TextTrimming.CharacterEllipsis;

                _rows.Children.Add(empty);
                _lineLabels.Add(empty);
                ApplyTheme();
                return;
            }

            bool firstSection = true;
            for (int sectionIndex = 0; sectionIndex < _sections.Length; sectionIndex++)
            {
                DetailSection section = _sections[sectionIndex];
                if (section == null)
                {
                    continue;
                }

                bool hasTitle = !string.IsNullOrEmpty(section.Title);
                bool hasLines = section.Lines != null && section.Lines.Length > 0;
                if (!hasTitle && !hasLines)
                {
                    continue;
                }

                if (hasTitle)
                {
                    TextBlock title = new TextBlock();
                    title.Text = section.Title;
                    title.FontFamily = UiTheme.UiFont;
                    title.FontSize = 13.5;
                    title.FontWeight = FontWeights.SemiBold;
                    title.Margin = new Thickness(
                        0,
                        firstSection ? 0 : 12,
                        0,
                        4);
                    title.TextTrimming = TextTrimming.CharacterEllipsis;

                    _rows.Children.Add(title);
                    _titleLabels.Add(title);
                }

                if (hasLines)
                {
                    for (int lineIndex = 0; lineIndex < section.Lines.Length; lineIndex++)
                    {
                        string text = section.Lines[lineIndex];
                        if (text == null)
                        {
                            continue;
                        }

                        TextBlock line = new TextBlock();
                        line.Text = text;
                        line.FontFamily = UiTheme.UiFont;
                        line.FontSize = 13;
                        line.Margin = new Thickness(0, 2, 0, 2);
                        line.TextWrapping = TextWrapping.Wrap;
                        line.TextTrimming = TextTrimming.CharacterEllipsis;

                        _rows.Children.Add(line);
                        _lineLabels.Add(line);
                    }
                }

                firstSection = false;
            }

            ApplyTheme();
        }
    }
}
