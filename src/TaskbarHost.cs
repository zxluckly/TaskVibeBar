using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VibeBar
{
    /// <summary>
    /// WPF 透明窗口 + SetParent 挂到 Shell_TrayWnd。窗口是任务栏的子窗口，
    /// 因而会随任务栏一起隐藏；详情面板不能使用此方式。
    /// </summary>
    internal sealed class TaskbarHost : Window
    {
        private readonly TaskbarStrip _strip;
        private bool _enabled;
        private bool _destroyed;
        private IntPtr _taskbar;
        private HwndSource _source;
        private int _maxWidth = 420;
        private int _minWidth = 120;
        private int _offsetX = 8;

        internal TaskbarHost(TaskbarStrip strip)
        {
            if (strip == null)
            {
                throw new ArgumentNullException("strip");
            }

            _strip = strip;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;
            Topmost = false;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // 注意：SetParent 后 WPF 的 Opacity 变更可能不再生效，宿主必须始终可见；
            // 透明效果由 TaskbarStrip 的 1/255 背景承担，不依赖窗口 Opacity。
            Opacity = 1;

            Content = strip;
            SizeChanged += delegate { SchedulePosition(); };
        }

        internal bool IsDestroyed
        {
            get { return _destroyed; }
        }

        internal TaskbarStrip Strip
        {
            get { return _strip; }
        }

        internal void SetVisible(bool visible)
        {
            if (_destroyed)
            {
                return;
            }

            _enabled = visible;
            if (!visible)
            {
                try
                {
                    Hide();
                }
                catch (InvalidOperationException)
                {
                    _destroyed = true;
                }

                return;
            }

            try
            {
                // Show() 之前创建句柄并写入 WS_EX_NOACTIVATE，避免 SetParent 后抢前台。
                EnsureNoActivateStyle();

                if (!IsVisible)
                {
                    Show();
                }

                AttachToTaskbar();
            }
            catch (InvalidOperationException)
            {
                // Explorer 重启时 TaskbarCreated 可能先于旧窗口的销毁到达；
                // 此时 Window 已被级联销毁，任何 Show/EnsureHandle 都会抛异常。
                // 标记失效，交由上层重建宿主。
                MarkDestroyed();
            }
        }

        internal void Reattach()
        {
            if (!_enabled || _destroyed)
            {
                return;
            }

            try
            {
                AttachToTaskbar();
            }
            catch (InvalidOperationException)
            {
                MarkDestroyed();
            }
        }

        private void MarkDestroyed()
        {
            _destroyed = true;
            _taskbar = IntPtr.Zero;
        }

        internal void SetOffsetX(int offsetX)
        {
            if (offsetX < 0)
            {
                offsetX = 0;
            }
            else if (offsetX > 400)
            {
                offsetX = 400;
            }

            _offsetX = offsetX;

            if (_enabled && !_destroyed)
            {
                UpdatePosition();
            }
        }

        internal void SetWidthLimits(int maxWidth, int minWidth)
        {
            if (maxWidth < 1)
            {
                maxWidth = 420;
            }

            if (minWidth < 1)
            {
                minWidth = 120;
            }

            if (minWidth > maxWidth)
            {
                minWidth = maxWidth;
            }

            _maxWidth = maxWidth;
            _minWidth = minWidth;

            if (_enabled && !_destroyed)
            {
                UpdatePosition();
            }
        }

        /// <summary>
        /// 供 1s 定时器调用：任务栏/通知区宽度变化时重新折叠或恢复显示。
        /// </summary>
        internal void RefreshLayout()
        {
            if (_enabled && !_destroyed)
            {
                UpdatePosition();
            }
        }

        internal Drawing.Rectangle GetScreenBounds()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            NativeMethods.RECT rect;
            if (handle == IntPtr.Zero
                || !NativeMethods.IsWindow(handle)
                || !NativeMethods.GetWindowRect(handle, out rect))
            {
                return Drawing.Rectangle.Empty;
            }

            return Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _source = PresentationSource.FromVisual(this) as HwndSource;
            if (_source != null)
            {
                _source.AddHook(WndProc);
            }

            EnsureNoActivateStyle();

            // 与旧的电量工具一致：在 Show() 完成前（OnSourceInitialized 阶段）就挂到任务栏，
            // 让 WPF 直接以子窗口身份完成首次合成；只在 Show() 之后 SetParent 可能留下不可见的合成表面。
            AttachToTaskbar();
        }

        private void EnsureNoActivateStyle()
        {
            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            ApplyNoActivateStyle(handle);
        }

        private static void ApplyNoActivateStyle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
            if ((style & NativeMethods.WS_EX_NOACTIVATE) == 0)
            {
                NativeMethods.SetWindowLong(
                    handle,
                    NativeMethods.GWL_EXSTYLE,
                    style | NativeMethods.WS_EX_NOACTIVATE);
            }
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            AttachToTaskbar();
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == NativeMethods.WM_DESTROY
                || message == NativeMethods.WM_NCDESTROY)
            {
                _destroyed = true;
                _taskbar = IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private void SchedulePosition()
        {
            if (_destroyed)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(UpdatePosition));
        }

        private void AttachToTaskbar()
        {
            if (!_enabled || _destroyed)
            {
                return;
            }

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            {
                return;
            }

            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero)
            {
                return;
            }

            _taskbar = taskbar;
            EnsureAttached(handle, taskbar);
            UpdatePosition();
        }

        private void EnsureAttached(IntPtr handle, IntPtr taskbar)
        {
            if (NativeMethods.GetParent(handle) == taskbar)
            {
                return;
            }

            NativeMethods.SetParent(handle, taskbar);
            int style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_STYLE);
            style = (style & ~NativeMethods.WS_POPUP) | NativeMethods.WS_CHILD;
            NativeMethods.SetWindowLong(handle, NativeMethods.GWL_STYLE, style);
        }

        private void UpdatePosition()
        {
            if (!_enabled || _destroyed)
            {
                return;
            }

            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            {
                return;
            }

            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !NativeMethods.IsWindow(taskbar))
            {
                return;
            }

            _taskbar = taskbar;
            // WPF 在初始化/布局后可能重置父子关系，每次定位前都校正。
            EnsureAttached(handle, taskbar);

            NativeMethods.RECT taskbarRect;
            if (!NativeMethods.GetWindowRect(taskbar, out taskbarRect))
            {
                return;
            }

            IntPtr notify = NativeMethods.FindWindowEx(
                taskbar,
                IntPtr.Zero,
                "TrayNotifyWnd",
                null);

            int rightEdge = taskbarRect.Right - 220;
            if (notify != IntPtr.Zero)
            {
                NativeMethods.RECT notifyRect;
                if (NativeMethods.GetWindowRect(notify, out notifyRect)
                    && notifyRect.Left > taskbarRect.Left)
                {
                    rightEdge = notifyRect.Left - _offsetX;
                }
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
            double scaleY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;

            // 可用宽度 = 通知区左缘 - 8 - 任务栏左缘 - 16（物理像素）。
            int availablePixels = rightEdge - taskbarRect.Left - 16;
            if (availablePixels < 0)
            {
                availablePixels = 0;
            }

            double availableDip = availablePixels / scaleX;
            if (availableDip > _maxWidth)
            {
                availableDip = _maxWidth;
            }

            if (availableDip < 0)
            {
                availableDip = 0;
            }

            // 折叠必须在测量窗口宽度之前完成；低于 minWidth 时只保留额度。
            _strip.SetAvailableWidth(availableDip, _minWidth);

            try
            {
                if (_strip.ContentHidden)
                {
                    if (IsVisible)
                    {
                        Hide();
                    }

                    return;
                }

                if (!IsVisible)
                {
                    Show();
                }
            }
            catch (InvalidOperationException)
            {
                // 窗口已被 Explorer 级联销毁，交给上层重建。
                MarkDestroyed();
                return;
            }

            UpdateLayout();

            double actualWidth = ActualWidth;
            double actualHeight = ActualHeight;
            if (double.IsNaN(actualWidth) || double.IsNaN(actualHeight)
                || actualWidth <= 0 || actualHeight <= 0)
            {
                return;
            }

            int width = (int)Math.Ceiling(actualWidth * scaleX);
            int height = (int)Math.Ceiling(actualHeight * scaleY);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            int x = rightEdge - width;
            if (x < taskbarRect.Left + 8)
            {
                x = taskbarRect.Left + 8;
            }

            int y = taskbarRect.Top + (taskbarHeight - height) / 2;

            NativeMethods.POINT clientPoint = new NativeMethods.POINT();
            clientPoint.X = x;
            clientPoint.Y = y;
            if (!NativeMethods.ScreenToClient(taskbar, ref clientPoint))
            {
                return;
            }

            NativeMethods.SetWindowPos(
                handle,
                IntPtr.Zero,
                clientPoint.X,
                clientPoint.Y,
                width,
                height,
                NativeMethods.SWP_NOZORDER
                    | NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_SHOWWINDOW);

            // 不再等待首次定位才恢复不透明度：SetParent 后该属性可能无法再更新。
            Opacity = 1;
        }
    }

    /// <summary>
    /// 独立的隐藏顶层窗口，只用于接收 Explorer 广播的 TaskbarCreated。
    /// 它不能是任务栏的子窗口，否则 Explorer 重启时会被一并销毁。
    /// </summary>
    internal sealed class TaskbarWatcher : Forms.NativeWindow, IDisposable
    {
        private readonly uint _taskbarCreatedMessage;

        internal event EventHandler TaskbarCreated;

        internal TaskbarWatcher()
        {
            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

            Forms.CreateParams createParams = new Forms.CreateParams();
            createParams.ClassName = "STATIC";
            createParams.Style = 0;
            CreateHandle(createParams);
        }

        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == (int)_taskbarCreatedMessage)
            {
                EventHandler handler = TaskbarCreated;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}
