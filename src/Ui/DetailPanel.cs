using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
        private readonly Button _closeButton;
        private readonly ScrollViewer _scroll;
        private readonly StackPanel _rows;
        private readonly Button _settingsButton;
        private readonly List<TextBlock> _lineLabels = new List<TextBlock>();
        private readonly List<TextBlock> _titleLabels = new List<TextBlock>();
        private readonly List<Button> _actionButtons = new List<Button>();
        private readonly DispatcherTimer _showTimer;
        private readonly DispatcherTimer _hideTimer;
        private readonly DispatcherTimer _visibilityTimer;
        private readonly DispatcherTimer _cursorTimer;

        private DetailSection[] _sections = new DetailSection[0];
        private Drawing.Rectangle _anchor = Drawing.Rectangle.Empty;
        private IntPtr _taskbar = IntPtr.Zero;
        private DateTime _outsideSince = DateTime.MinValue;
        private bool _panelVisible;
        private bool _pinned;
        private bool _disposed;

        internal event EventHandler SettingsRequested;

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
            Width = UiTheme.PanelWidth + UiTheme.PanelShadowMargin * 2.0;
            MaxHeight = UiTheme.PanelMaxHeight;
            Opacity = 0;
            Focusable = false;

            _root = new Border();
            _root.CornerRadius = new CornerRadius(UiTheme.PanelCornerRadius);
            _root.BorderThickness = new Thickness(1);
            _root.Padding = new Thickness(16);
            _root.Margin = new Thickness(UiTheme.PanelShadowMargin);

            DropShadowEffect shadow = new DropShadowEffect();
            shadow.Color = Colors.Black;
            shadow.BlurRadius = 18;
            shadow.Opacity = 0.22;
            shadow.ShadowDepth = 2;
            shadow.Direction = 270;
            _root.Effect = shadow;

            _heading = new TextBlock();
            _heading.Text = "VibeBar";
            _heading.FontFamily = UiTheme.UiFont;
            _heading.FontSize = 14;
            _heading.FontWeight = FontWeights.SemiBold;
            _heading.Margin = new Thickness(0, 0, 0, 0);
            _heading.VerticalAlignment = VerticalAlignment.Center;

            // 面板是无激活窗口，没有键盘焦点；给一个显式关闭按钮，
            // 避免用户只能「再点一次段」才能收起面板。
            _closeButton = CreateButton("×", delegate { HideNow(); });
            _closeButton.FontSize = 15;
            _closeButton.Padding = new Thickness(8, 0, 8, 2);
            _closeButton.Margin = new Thickness(0);
            _closeButton.HorizontalAlignment = HorizontalAlignment.Right;
            _closeButton.VerticalAlignment = VerticalAlignment.Center;
            _closeButton.ToolTip = "关闭面板";

            DockPanel header = new DockPanel();
            header.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(_closeButton, Dock.Right);
            header.Children.Add(_closeButton);
            header.Children.Add(_heading);

            _rows = new StackPanel();

            _scroll = new ScrollViewer();
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.PanningMode = PanningMode.VerticalOnly;
            _scroll.Focusable = false;
            _scroll.MaxHeight = UiTheme.PanelMaxHeight;
            _scroll.Content = _rows;

            _settingsButton = CreateButton("设置", delegate
            {
                EventHandler handler = SettingsRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            });
            _settingsButton.HorizontalAlignment = HorizontalAlignment.Left;
            _settingsButton.Margin = new Thickness(0, 12, 0, 0);

            DockPanel dock = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            DockPanel.SetDock(_settingsButton, Dock.Bottom);
            dock.Children.Add(_settingsButton);
            dock.Children.Add(_scroll);

            _root.Child = dock;
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

                if (!IsTaskbarEffectivelyVisible(_taskbar))
                {
                    return;
                }

                ShowPanel(_taskbar);
            };

            _hideTimer = new DispatcherTimer();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(300);
            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                if (!_pinned)
                {
                    HideNow();
                }
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

                if (!IsTaskbarEffectivelyVisible(_taskbar))
                {
                    HideNow();
                }
            };

            // 固定（点击段）后的兜底：鼠标离开面板与悬浮条 4 秒后自动收起，
            // 避免面板「锁死」在屏幕上只能靠再点一次段才能关掉。
            _cursorTimer = new DispatcherTimer();
            _cursorTimer.Interval = TimeSpan.FromMilliseconds(400);
            _cursorTimer.Tick += delegate { CheckCursorOutside(); };

            ApplyTheme();
        }

        internal bool IsPinned
        {
            get { return _pinned; }
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

            if (_panelVisible)
            {
                Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(delegate
                    {
                        if (_disposed || !_panelVisible)
                        {
                            return;
                        }

                        UpdateLayout();
                        if (IsTaskbarEffectivelyVisible(_taskbar))
                        {
                            PositionPanel(_taskbar);
                        }
                    }));
            }
        }

        internal void ScheduleShow(Drawing.Rectangle anchor, IntPtr taskbar)
        {
            if (_disposed)
            {
                return;
            }

            _anchor = anchor;
            if (taskbar != IntPtr.Zero)
            {
                _taskbar = taskbar;
            }

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
            if (!_panelVisible || _pinned)
            {
                return;
            }

            _hideTimer.Stop();
            _hideTimer.Start();
        }

        internal void SetPinned(bool pinned)
        {
            _pinned = pinned;
            if (pinned)
            {
                _hideTimer.Stop();
                _showTimer.Stop();
                _outsideSince = DateTime.MinValue;
                _cursorTimer.Start();
            }
            else
            {
                _cursorTimer.Stop();
            }
        }

        internal void ShowPinned(Drawing.Rectangle anchor, IntPtr taskbar)
        {
            if (_disposed)
            {
                return;
            }

            _anchor = anchor;
            if (taskbar != IntPtr.Zero)
            {
                _taskbar = taskbar;
            }

            _pinned = true;
            _showTimer.Stop();
            _hideTimer.Stop();
            _outsideSince = DateTime.MinValue;

            if (IsTaskbarEffectivelyVisible(_taskbar))
            {
                ShowPanel(_taskbar);
                _cursorTimer.Start();
            }
        }

        internal void SetTopmost(bool topmost)
        {
            if (!_disposed)
            {
                Topmost = topmost;
            }
        }

        internal void HideNow()
        {
            _showTimer.Stop();
            _hideTimer.Stop();
            _visibilityTimer.Stop();
            _cursorTimer.Stop();
            _outsideSince = DateTime.MinValue;
            _panelVisible = false;
            _pinned = false;

            if (_disposed)
            {
                return;
            }

            Opacity = 0;
            Hide();
        }

        /// <summary>
        /// 固定状态下的兜底收起：鼠标既不在面板、也不在悬浮条上超过 4 秒，
        /// 自动关闭并取消固定（面板无激活窗口，无法靠失焦事件判断）。
        /// </summary>
        private void CheckCursorOutside()
        {
            if (_disposed || !_panelVisible || !_pinned)
            {
                _cursorTimer.Stop();
                return;
            }

            Drawing.Point cursor;
            try
            {
                cursor = Forms.Cursor.Position;
            }
            catch
            {
                return;
            }

            Drawing.Rectangle panelRect = GetPanelScreenRect();
            bool insidePanel = panelRect.Width > 0 && panelRect.Contains(cursor);
            bool insideAnchor = _anchor.Width > 0 && _anchor.Height > 0
                && _anchor.Contains(cursor);

            if (insidePanel || insideAnchor)
            {
                _outsideSince = DateTime.MinValue;
                return;
            }

            if (_outsideSince == DateTime.MinValue)
            {
                _outsideSince = DateTime.UtcNow;
                return;
            }

            if ((DateTime.UtcNow - _outsideSince).TotalSeconds >= 4.0)
            {
                HideNow();
            }
        }

        private Drawing.Rectangle GetPanelScreenRect()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            {
                return Drawing.Rectangle.Empty;
            }

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(handle, out rect))
            {
                return Drawing.Rectangle.Empty;
            }

            return Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
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

            ApplyButtonTheme(_settingsButton);
            ApplyButtonTheme(_closeButton);

            for (int index = 0; index < _actionButtons.Count; index++)
            {
                ApplyButtonTheme(_actionButtons[index]);
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
            _cursorTimer.Stop();
            _panelVisible = false;
            _pinned = false;

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
            }

            // 工作区必须取「悬浮条所在显示器」，不能取主屏任务栏所在显示器，
            // 否则副屏模式下详情面板会被夹到主屏上（或被主屏边界裁掉）。
            Drawing.Rectangle work;
            try
            {
                work = anchor.Width > 0 && anchor.Height > 0
                    ? Forms.Screen.FromRectangle(anchor).WorkingArea
                    : Forms.Screen.FromHandle(taskbar).WorkingArea;
            }
            catch
            {
                work = Forms.Screen.PrimaryScreen.WorkingArea;
            }

            if (anchor.Width <= 0 || anchor.Height <= 0)
            {
                anchor = work;
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
            _actionButtons.Clear();

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
                bool hasActions = section.Actions != null && section.Actions.Length > 0;
                if (!hasTitle && !hasLines && !hasActions)
                {
                    continue;
                }

                StackPanel sectionPanel = new StackPanel();
                sectionPanel.Margin = new Thickness(0, firstSection ? 0 : 12, 0, 0);

                if (hasTitle)
                {
                    TextBlock title = new TextBlock();
                    title.Text = section.Title;
                    title.FontFamily = UiTheme.UiFont;
                    title.FontSize = 13.5;
                    title.FontWeight = FontWeights.SemiBold;
                    title.Margin = new Thickness(0, 0, 0, 4);
                    title.TextTrimming = TextTrimming.CharacterEllipsis;

                    sectionPanel.Children.Add(title);
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

                        sectionPanel.Children.Add(line);
                        _lineLabels.Add(line);
                    }
                }

                if (hasActions)
                {
                    WrapPanel actionRow = new WrapPanel();
                    actionRow.Orientation = Orientation.Horizontal;
                    actionRow.Margin = new Thickness(0, 8, 0, 0);

                    for (int actionIndex = 0;
                        actionIndex < section.Actions.Length;
                        actionIndex++)
                    {
                        DetailAction action = section.Actions[actionIndex];
                        if (action == null || string.IsNullOrEmpty(action.Label))
                        {
                            continue;
                        }

                        Button button = CreateButton(action.Label, action.Handler);
                        _actionButtons.Add(button);
                        actionRow.Children.Add(button);
                    }

                    if (actionRow.Children.Count > 0)
                    {
                        sectionPanel.Children.Add(actionRow);
                    }
                }

                _rows.Children.Add(sectionPanel);
                firstSection = false;
            }

            if (firstSection)
            {
                TextBlock empty = new TextBlock();
                empty.Text = "暂无数据";
                empty.FontFamily = UiTheme.UiFont;
                empty.FontSize = 13;
                empty.Margin = new Thickness(0, 2, 0, 2);
                _rows.Children.Add(empty);
                _lineLabels.Add(empty);
            }

            ApplyTheme();
        }

        private Button CreateButton(string label, Action handler)
        {
            Button button = new Button();
            button.Content = label;
            button.FontFamily = UiTheme.UiFont;
            button.FontSize = 12.5;
            button.Padding = new Thickness(10, 4, 10, 4);
            button.Margin = new Thickness(0, 0, 8, 0);
            button.Focusable = false;
            button.IsTabStop = false;
            button.Template = CreateButtonTemplate();

            if (handler == null)
            {
                button.IsEnabled = false;
                button.Cursor = Cursors.Arrow;
            }
            else
            {
                button.Cursor = Cursors.Hand;
                button.Click += delegate
                {
                    try
                    {
                        handler();
                    }
                    catch (Exception exception)
                    {
                        Log.Error("详情面板动作失败", exception);
                    }
                };
                button.MouseEnter += delegate
                {
                    SetButtonBackground(button, UiTheme.ButtonHover);
                };
                button.MouseLeave += delegate
                {
                    SetButtonBackground(button, UiTheme.ButtonBackground);
                };
                button.PreviewMouseLeftButtonDown += delegate
                {
                    SetButtonBackground(button, UiTheme.ButtonPressed);
                };
                button.PreviewMouseLeftButtonUp += delegate
                {
                    SetButtonBackground(button, UiTheme.ButtonHover);
                };
            }

            ApplyButtonTheme(button);
            return button;
        }

        private static void SetButtonBackground(Button button, Color color)
        {
            if (button == null || !button.IsEnabled)
            {
                return;
            }

            button.Background = new SolidColorBrush(color);
        }

        private static void ApplyButtonTheme(Button button)
        {
            if (button == null)
            {
                return;
            }

            bool enabled = button.IsEnabled;
            button.Background = new SolidColorBrush(
                enabled ? UiTheme.ButtonBackground : Colors.Transparent);
            button.BorderBrush = new SolidColorBrush(UiTheme.Border);
            button.BorderThickness = new Thickness(1);
            button.Foreground = new SolidColorBrush(
                enabled ? UiTheme.TextPrimary : UiTheme.TextDisabled);
            button.Opacity = enabled ? 1.0 : 0.55;
        }

        private static ControlTemplate CreateButtonTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(
                Border.CornerRadiusProperty,
                new CornerRadius(6));
            border.SetValue(
                Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(
                Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(
                Border.BorderThicknessProperty,
                new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);

            FrameworkElementFactory presenter =
                new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(
                ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(
                ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            presenter.SetValue(
                ContentPresenter.MarginProperty,
                new TemplateBindingExtension(Control.PaddingProperty));

            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }
    }
}
