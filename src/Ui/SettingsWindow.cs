using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VibeBar
{
    /// <summary>
    /// 代码构建的设置窗口（无 XAML）。API Key 留空表示保持原值；
    /// Key 只写入 DPAPI 凭证，settings.json 中不含 Key。
    /// </summary>
    internal sealed class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly SettingsStore _settingsStore;
        private readonly HookInstaller _hookInstaller;

        private readonly PasswordBox _apiKeyBox;
        private readonly TextBox _baseUrlBox;
        private readonly TextBox _repoPathBox;
        private readonly ComboBox _intervalBox;
        private readonly CheckBox _showTaskbarBox;
        private readonly CheckBox _privacyBox;
        private readonly CheckBox _claudeKeyBox;

        private readonly ComboBox _anchorBox;
        private readonly ComboBox _monitorBox;
        private readonly TextBlock _offsetLabel;
        private readonly TextBox _offsetBox;
        private readonly TextBox _maxWidthBox;
        private readonly List<CheckBox> _segmentChecks = new List<CheckBox>();
        private readonly CheckBox _hoverThemeBox;
        private readonly TextBox _hoverColorBox;
        private readonly Slider _hoverOpacitySlider;
        private readonly TextBlock _hoverOpacityLabel;
        private readonly Border _hoverPreview;
        private readonly TextBlock _hookStatusText;

        // 扩展段：关注端口 / 端口与 PR 刷新间隔 / 自定义命令。
        private readonly TextBox _portsBox;
        private readonly ComboBox _portIntervalBox;
        private readonly ComboBox _prIntervalBox;
        private readonly CustomCommandEditor _commandEditor;

        // 外部段（插件协议 v1）：启用开关 / 最大段数 / 默认存活。
        private readonly CheckBox _externalEnabledBox;
        private readonly TextBox _externalMaxSegmentsBox;
        private readonly TextBox _externalTtlBox;

        private readonly TextBlock _validationText;
        private readonly DispatcherTimer _validationTimer;

        // 两栏布局：侧边栏导航 + 内容区（页头 / 每页独立滚动区 / 固定操作栏）。
        // 控件实例只在构造函数里创建一次，切页只改 Visibility。
        private readonly List<Border> _cards = new List<Border>();
        private readonly List<NavEntry> _navEntries = new List<NavEntry>();
        private readonly List<ScrollViewer> _pageScrollers = new List<ScrollViewer>();

        private static readonly string[] PageTitles = { "账号", "常规", "扩展段", "数据与集成" };

        private Border _actionBarBorder;
        private Border _sidebarBorder;
        private TextBlock _brandText;
        private TextBlock _brandSubtitle;
        private TextBlock _pageTitle;
        private int _pageIndex = -1;

        private Drawing.Rectangle _targetWork = Drawing.Rectangle.Empty;
        private bool _placementHooked;

        internal event EventHandler Saved;

        /// <summary>一个侧边栏导航项（圆角 pill + 图标 + 文字）。</summary>
        private sealed class NavEntry
        {
            internal Border Root;
            internal TextBlock Icon;
            internal TextBlock Label;
            internal int Index;
            internal bool Selected;
        }

        internal SettingsWindow(
            AppSettings settings,
            SettingsStore settingsStore,
            IList<SegmentDescriptor> segmentDescriptors)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if (settingsStore == null)
            {
                throw new ArgumentNullException("settingsStore");
            }

            _settings = settings;
            _settingsStore = settingsStore;
            _hookInstaller = new HookInstaller();

            Title = "VibeBar 设置";
            Width = 900;
            Height = 680;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(UiTheme.WindowBackground);
            Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;

            _apiKeyBox = new PasswordBox();
            _apiKeyBox.Height = 28;
            _apiKeyBox.Padding = new Thickness(6, 4, 6, 4);

            _baseUrlBox = new TextBox();
            _baseUrlBox.Height = 28;
            _baseUrlBox.Padding = new Thickness(6, 4, 6, 4);
            _baseUrlBox.Text = string.IsNullOrEmpty(settings.Quota.BaseUrl)
                ? "https://api.deepseek.com"
                : settings.Quota.BaseUrl;

            _repoPathBox = new TextBox();
            _repoPathBox.Height = 28;
            _repoPathBox.Padding = new Thickness(6, 4, 6, 4);
            _repoPathBox.Text = settings.Git.RepoPath == null
                ? string.Empty
                : settings.Git.RepoPath;

            _intervalBox = new ComboBox();
            _intervalBox.Height = 30;
            _intervalBox.Width = 160;
            _intervalBox.HorizontalAlignment = HorizontalAlignment.Left;
            _intervalBox.Template = CreateComboBoxTemplate();
            AddIntervalItem(60);
            AddIntervalItem(120);
            AddIntervalItem(300);
            SelectInterval(settings.Quota.RefreshSeconds);

            _showTaskbarBox = new CheckBox();
            _showTaskbarBox.Content = "显示任务栏";
            _showTaskbarBox.IsChecked = settings.General.ShowTaskbar;
            _showTaskbarBox.Margin = new Thickness(0, 8, 0, 0);

            _privacyBox = new CheckBox();
            _privacyBox.Content = "隐私模式（金额显示为 ¥•••）";
            _privacyBox.IsChecked = settings.General.PrivacyMode;
            _privacyBox.Margin = new Thickness(0, 8, 0, 0);

            _claudeKeyBox = new CheckBox();
            _claudeKeyBox.Content = "从 Claude Code 配置读取 Key（ANTHROPIC_AUTH_TOKEN）";
            _claudeKeyBox.IsChecked = settings.Quota.ReadKeyFromClaudeSettings;
            _claudeKeyBox.Margin = new Thickness(0, 8, 0, 0);

            _anchorBox = new ComboBox();
            _anchorBox.Height = 30;
            _anchorBox.Width = 180;
            _anchorBox.HorizontalAlignment = HorizontalAlignment.Left;
            _anchorBox.Template = CreateComboBoxTemplate();

            ComboBoxItem rightAnchor = new ComboBoxItem();
            rightAnchor.Content = "靠右（托盘左侧）";
            rightAnchor.Tag = "right";
            _anchorBox.Items.Add(rightAnchor);

            ComboBoxItem leftAnchor = new ComboBoxItem();
            leftAnchor.Content = "靠左（任务栏左侧）";
            leftAnchor.Tag = "left";
            _anchorBox.Items.Add(leftAnchor);

            SelectAnchor(settings.Ui.Anchor);
            _anchorBox.SelectionChanged += delegate
            {
                UpdateOffsetLabel();
            };

            _monitorBox = new ComboBox();
            _monitorBox.Height = 30;
            _monitorBox.Width = 180;
            _monitorBox.HorizontalAlignment = HorizontalAlignment.Left;
            _monitorBox.Template = CreateComboBoxTemplate();

            ComboBoxItem primaryMonitor = new ComboBoxItem();
            primaryMonitor.Content = "主屏任务栏";
            primaryMonitor.Tag = "primary";
            _monitorBox.Items.Add(primaryMonitor);

            ComboBoxItem secondaryMonitor = new ComboBoxItem();
            secondaryMonitor.Content = "副屏任务栏";
            secondaryMonitor.Tag = "secondary";
            _monitorBox.Items.Add(secondaryMonitor);

            SelectMonitor(settings.Ui.Monitor);

            _offsetLabel = new TextBlock();
            _offsetLabel.Width = 150;
            _offsetLabel.VerticalAlignment = VerticalAlignment.Center;
            UpdateOffsetLabel();

            _offsetBox = new TextBox();
            _offsetBox.Height = 28;
            _offsetBox.Width = 100;
            _offsetBox.HorizontalAlignment = HorizontalAlignment.Left;
            _offsetBox.Text = settings.Ui.OffsetX.ToString();

            _maxWidthBox = new TextBox();
            _maxWidthBox.Height = 28;
            _maxWidthBox.Width = 100;
            _maxWidthBox.HorizontalAlignment = HorizontalAlignment.Left;
            _maxWidthBox.Text = settings.Ui.MaxWidth.ToString();

            IList<SegmentDescriptor> descriptors = segmentDescriptors;
            if (descriptors == null || descriptors.Count == 0)
            {
                descriptors = new List<SegmentDescriptor>();
                descriptors.Add(new SegmentDescriptor("quota", "额度", 10));
                descriptors.Add(new SegmentDescriptor("git", "Git", 20));
                descriptors.Add(new SegmentDescriptor("agent", "Claude 会话", 30));
            }

            for (int index = 0; index < descriptors.Count; index++)
            {
                SegmentDescriptor descriptor = descriptors[index];
                if (descriptor == null || string.IsNullOrEmpty(descriptor.Id))
                {
                    continue;
                }

                CheckBox box = new CheckBox();
                box.Content = descriptor.DisplayName;
                box.Tag = descriptor.Id;
                box.IsChecked = settings.Ui.IsSegmentEnabled(descriptor.Id);
                box.Margin = new Thickness(0, 4, 18, 4);
                _segmentChecks.Add(box);
            }

            _hoverThemeBox = new CheckBox();
            _hoverThemeBox.Content = "跟随系统主题色";
            _hoverThemeBox.IsChecked = string.IsNullOrEmpty(settings.Ui.HoverColor);

            _hoverColorBox = new TextBox();
            _hoverColorBox.Height = 28;
            _hoverColorBox.Width = 120;
            _hoverColorBox.HorizontalAlignment = HorizontalAlignment.Left;
            _hoverColorBox.Text = settings.Ui.HoverColor;
            _hoverColorBox.ToolTip = "格式 #RRGGBB，例如 #FF8800";
            _hoverColorBox.TextChanged += delegate
            {
                UpdateHoverControls();
                UpdateHoverPreview();
            };

            _hoverThemeBox.Checked += delegate
            {
                UpdateHoverControls();
                UpdateHoverPreview();
            };
            _hoverThemeBox.Unchecked += delegate
            {
                UpdateHoverControls();
                UpdateHoverPreview();
            };

            _hoverOpacitySlider = new Slider();
            _hoverOpacitySlider.Minimum = 0;
            _hoverOpacitySlider.Maximum = 100;
            _hoverOpacitySlider.Width = 220;
            _hoverOpacitySlider.IsSnapToTickEnabled = true;
            _hoverOpacitySlider.TickFrequency = 1;
            _hoverOpacitySlider.VerticalAlignment = VerticalAlignment.Center;
            _hoverOpacitySlider.Value = settings.Ui.HoverOpacity * 100.0 / 255.0;
            _hoverOpacitySlider.ValueChanged += delegate
            {
                UpdateHoverControls();
                UpdateHoverPreview();
            };

            _hoverOpacityLabel = new TextBlock();
            _hoverOpacityLabel.VerticalAlignment = VerticalAlignment.Center;
            _hoverOpacityLabel.Margin = new Thickness(8, 0, 0, 0);

            _hoverPreview = new Border();
            _hoverPreview.Width = 48;
            _hoverPreview.Height = 24;
            _hoverPreview.CornerRadius = new CornerRadius(4);
            _hoverPreview.BorderThickness = new Thickness(1);
            _hoverPreview.BorderBrush = new SolidColorBrush(UiTheme.Border);
            _hoverPreview.Margin = new Thickness(10, 0, 0, 0);
            _hoverPreview.VerticalAlignment = VerticalAlignment.Center;

            _externalEnabledBox = new CheckBox();
            _externalEnabledBox.Content = "启用外部段";
            _externalEnabledBox.IsChecked = settings.External.Enabled;
            _externalEnabledBox.Margin = new Thickness(0, 4, 0, 4);
            _externalEnabledBox.Checked += delegate { UpdateExternalControls(); };
            _externalEnabledBox.Unchecked += delegate { UpdateExternalControls(); };

            _externalMaxSegmentsBox = new TextBox();
            _externalMaxSegmentsBox.Height = 28;
            _externalMaxSegmentsBox.Width = 80;
            _externalMaxSegmentsBox.HorizontalAlignment = HorizontalAlignment.Left;
            _externalMaxSegmentsBox.Text = settings.External.MaxSegments.ToString(
                CultureInfo.InvariantCulture);
            _externalMaxSegmentsBox.TextChanged += delegate { ClearValidationError(); };

            _externalTtlBox = new TextBox();
            _externalTtlBox.Height = 28;
            _externalTtlBox.Width = 80;
            _externalTtlBox.HorizontalAlignment = HorizontalAlignment.Left;
            _externalTtlBox.Text = settings.External.DefaultTtlSeconds.ToString(
                CultureInfo.InvariantCulture);
            _externalTtlBox.TextChanged += delegate { ClearValidationError(); };

            StyleCheckBoxes();
            UpdateHoverControls();
            UpdateHoverPreview();
            UpdateExternalControls();

            _hookStatusText = new TextBlock();
            _hookStatusText.TextWrapping = TextWrapping.Wrap;
            _hookStatusText.Margin = new Thickness(0, 4, 0, 0);
            _hookStatusText.Opacity = 0.85;
            RefreshHookStatus();

            _portsBox = new TextBox();
            _portsBox.Height = 58;
            _portsBox.AcceptsReturn = true;
            _portsBox.TextWrapping = TextWrapping.Wrap;
            _portsBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _portsBox.Padding = new Thickness(6, 4, 6, 4);
            _portsBox.VerticalAlignment = VerticalAlignment.Top;
            _portsBox.Text = SettingsValidation.FormatPorts(settings.Ports.WatchPorts);
            _portsBox.TextChanged += delegate { ClearValidationError(); };

            _portIntervalBox = CreateSecondsCombo(
                new int[] { 3, 5, 10, 30 },
                settings.Ports.PollSeconds,
                5);

            _prIntervalBox = CreateSecondsCombo(
                new int[] { 30, 60, 120, 300 },
                settings.Pr.PollSeconds,
                60);

            _commandEditor = new CustomCommandEditor(settings.Custom.Commands);

            _validationText = new TextBlock();
            _validationText.Foreground =
                new SolidColorBrush(UiTheme.RoleColor(ColorRole.Critical));
            _validationText.TextWrapping = TextWrapping.Wrap;
            _validationText.FontSize = 12;
            _validationText.Margin = new Thickness(0, 0, 0, 0);
            _validationText.Visibility = Visibility.Collapsed;

            _validationTimer = new DispatcherTimer();
            _validationTimer.Interval = TimeSpan.FromSeconds(3);
            _validationTimer.Tick += delegate
            {
                _validationTimer.Stop();
                ClearValidationError();
            };

            // 两栏：左 190 侧边栏（固定），右内容区（自适应）。
            Grid shell = new Grid();
            ColumnDefinition sidebarColumn = new ColumnDefinition();
            sidebarColumn.Width = new GridLength(190);
            ColumnDefinition contentColumn = new ColumnDefinition();
            contentColumn.Width = new GridLength(1, GridUnitType.Star);
            shell.ColumnDefinitions.Add(sidebarColumn);
            shell.ColumnDefinitions.Add(contentColumn);

            FrameworkElement sidebar = BuildSidebar();
            Grid.SetColumn(sidebar, 0);
            shell.Children.Add(sidebar);

            FrameworkElement contentArea = BuildContentArea();
            Grid.SetColumn(contentArea, 1);
            shell.Children.Add(contentArea);

            Content = shell;

            // 高度仍受工作区约束，避免小屏（或高缩放）下窗口超出屏幕。
            double workAreaHeight = SystemParameters.WorkArea.Height;
            MaxHeight = Math.Max(420.0, workAreaHeight - 40.0);

            SelectPage(0);

            // 主题可能在设置窗口打开期间切换（系统深浅色），统一在窗口级重刷。
            Theme.Changed += OnThemeChanged;
            Closed += delegate { Theme.Changed -= OnThemeChanged; };

            // 标题栏是非客户区，WPF 画不到，只能等 HWND 就绪后交给 DWM。
            SourceInitialized += delegate { ApplyWindowChromeTheme(); };
            ApplyTheme();
        }

        /// <summary>
        /// 把设置窗口放到悬浮条所在显示器居中（副屏场景不要总弹在主屏）。
        /// </summary>
        internal void PlaceOnMonitor(Drawing.Rectangle anchor)
        {
            if (anchor.Width <= 0 || anchor.Height <= 0)
            {
                return;
            }

            try
            {
                _targetWork = Forms.Screen.FromRectangle(anchor).WorkingArea;
            }
            catch
            {
                _targetWork = Drawing.Rectangle.Empty;
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;

            if (IsLoaded)
            {
                ApplyPlacement();
                return;
            }

            if (!_placementHooked)
            {
                _placementHooked = true;
                Loaded += delegate { ApplyPlacement(); };
            }
        }

        private void ApplyPlacement()
        {
            if (_targetWork.Width <= 0 || _targetWork.Height <= 0)
            {
                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
            double scaleY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;

            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : 420;

            Left = _targetWork.Left / scaleX
                + (_targetWork.Width / scaleX - width) / 2.0;
            Top = _targetWork.Top / scaleY
                + (_targetWork.Height / scaleY - height) / 2.0;
        }

        /// <summary>
        /// ComboBox 的默认模板用写死的浅色渐变绘制，深色主题下会出现
        /// 「白底浅灰字」。这里用代码构建一个跟随主题的最小模板：
        /// 边框 + 选中项文字 + ▾ + 下拉列表。
        /// internal static：自定义命令编辑器复用同一套模板，避免两份实现漂移。
        /// </summary>
        internal static ControlTemplate CreateComboBoxTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ComboBox));

            FrameworkElementFactory grid = new FrameworkElementFactory(typeof(Grid));

            FrameworkElementFactory toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.SetValue(ToggleButton.FocusableProperty, false);
            toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
            toggle.SetValue(ToggleButton.TemplateProperty, CreateComboToggleTemplate());
            toggle.SetValue(
                Control.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            toggle.SetValue(
                Control.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));

            Binding openBinding = new Binding("IsDropDownOpen");
            openBinding.RelativeSource =
                new RelativeSource(RelativeSourceMode.TemplatedParent);
            openBinding.Mode = BindingMode.TwoWay;
            toggle.SetValue(ToggleButton.IsCheckedProperty, openBinding);
            grid.AppendChild(toggle);

            FrameworkElementFactory selection = new FrameworkElementFactory(
                typeof(ContentPresenter));
            selection.SetValue(
                ContentPresenter.ContentProperty,
                new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            selection.SetValue(
                ContentPresenter.MarginProperty,
                new Thickness(9, 0, 26, 0));
            selection.SetValue(
                ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            selection.SetValue(
                ContentPresenter.IsHitTestVisibleProperty,
                false);
            grid.AppendChild(selection);

            FrameworkElementFactory arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.SetValue(TextBlock.TextProperty, "▾");
            arrow.SetValue(TextBlock.FontSizeProperty, 11.0);
            arrow.SetValue(
                TextBlock.HorizontalAlignmentProperty,
                HorizontalAlignment.Right);
            arrow.SetValue(
                TextBlock.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            arrow.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 9, 0));
            arrow.SetValue(TextBlock.IsHitTestVisibleProperty, false);
            grid.AppendChild(arrow);

            FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Slide);

            Binding popupBinding = new Binding("IsDropDownOpen");
            popupBinding.RelativeSource =
                new RelativeSource(RelativeSourceMode.TemplatedParent);
            popupBinding.Mode = BindingMode.TwoWay;
            popup.SetValue(Popup.IsOpenProperty, popupBinding);

            FrameworkElementFactory popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(Border.MarginProperty, new Thickness(0, 2, 0, 0));
            popupBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);
            popupBorder.SetValue(
                Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            popupBorder.SetValue(
                Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));

            Binding widthBinding = new Binding("ActualWidth");
            widthBinding.RelativeSource =
                new RelativeSource(RelativeSourceMode.TemplatedParent);
            popupBorder.SetValue(FrameworkElement.MinWidthProperty, widthBinding);

            FrameworkElementFactory scroll = new FrameworkElementFactory(
                typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.MaxHeightProperty, 240.0);
            scroll.SetValue(
                ScrollViewer.VerticalScrollBarVisibilityProperty,
                ScrollBarVisibility.Auto);
            scroll.SetValue(
                ScrollViewer.HorizontalScrollBarVisibilityProperty,
                ScrollBarVisibility.Disabled);

            scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            popupBorder.AppendChild(scroll);
            popup.AppendChild(popupBorder);
            grid.AppendChild(popup);

            template.VisualTree = grid;
            return template;
        }

        private static ControlTemplate CreateComboToggleTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ToggleButton));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(
                Border.CornerRadiusProperty,
                new CornerRadius(4));
            border.SetValue(
                Border.BorderThicknessProperty,
                new Thickness(1));
            border.SetValue(
                Border.SnapsToDevicePixelsProperty,
                true);
            border.SetValue(
                Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(
                Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));

            template.VisualTree = border;
            return template;
        }

        private void ApplyComboTheme(ComboBox combo)
        {
            combo.Background = new SolidColorBrush(UiTheme.InputBackground);
            combo.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            combo.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
        }

        /// <summary>
        /// CheckBox 的默认模板同样写死浅色方框，深色主题下会露出白块。
        /// 这里换成「方框 + 勾 + 文字」的最小模板，颜色全部跟随主题。
        /// </summary>
        internal static ControlTemplate CreateCheckBoxTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(CheckBox));

            FrameworkElementFactory root = new FrameworkElementFactory(typeof(StackPanel));
            root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            FrameworkElementFactory box = new FrameworkElementFactory(typeof(Border));
            box.Name = "Box";
            box.SetValue(Border.WidthProperty, 15.0);
            box.SetValue(Border.HeightProperty, 15.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            box.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            FrameworkElementFactory check = new FrameworkElementFactory(typeof(TextBlock));
            check.Name = "Check";
            check.SetValue(TextBlock.TextProperty, "✓");
            check.SetValue(TextBlock.FontSizeProperty, 10.5);
            check.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            check.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(check);
            root.AppendChild(box);

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(7, 0, 0, 0));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            // 关掉助记符：文字里的下划线要原样显示（如 ANTHROPIC_AUTH_TOKEN），
            // 打开时 "_A" 会被当成快捷键标记而吞掉下划线。
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, false);
            root.AppendChild(content);

            template.VisualTree = root;

            Trigger checkedTrigger = new Trigger();
            checkedTrigger.Property = ToggleButton.IsCheckedProperty;
            checkedTrigger.Value = true;
            checkedTrigger.Setters.Add(new Setter(
                UIElement.VisibilityProperty,
                Visibility.Visible,
                "Check"));
            template.Triggers.Add(checkedTrigger);

            Trigger hoverTrigger = new Trigger();
            hoverTrigger.Property = UIElement.IsMouseOverProperty;
            hoverTrigger.Value = true;
            hoverTrigger.Setters.Add(new Setter(
                Border.BorderBrushProperty,
                new SolidColorBrush(UiTheme.Accent),
                "Box"));
            template.Triggers.Add(hoverTrigger);

            return template;
        }

        internal static void ApplyCheckTheme(CheckBox box)
        {
            box.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            box.Background = new SolidColorBrush(UiTheme.InputBackground);
            box.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
        }

        /// <summary>
        /// 按启用状态给按钮取色。默认 Button 模板在禁用时回落到系统画刷
        /// （深色主题下是白底），所以按钮必须同时换上 CreateButtonTemplate() 的模板。
        /// </summary>
        internal static void ApplyButtonTheme(Button button)
        {
            if (button == null)
            {
                return;
            }

            bool enabled = button.IsEnabled;
            button.Background = new SolidColorBrush(
                enabled ? UiTheme.ButtonSurface : UiTheme.InputBackground);
            button.Foreground = new SolidColorBrush(
                enabled ? UiTheme.AppTextPrimary : UiTheme.TextDisabled);
            button.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
            button.BorderThickness = new Thickness(1);
        }

        /// <summary>
        /// 代码构建的按钮模板：Border + ContentPresenter，颜色全部走 TemplateBinding，
        /// 禁用态由 Trigger 显式指定（不再落到系统白底）。写法参考 DetailPanel。
        /// </summary>
        internal static ControlTemplate CreateButtonTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Chrome";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
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
                FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(
                FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            presenter.SetValue(
                FrameworkElement.MarginProperty,
                new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(presenter);

            template.VisualTree = border;

            Trigger hoverTrigger = new Trigger();
            hoverTrigger.Property = UIElement.IsMouseOverProperty;
            hoverTrigger.Value = true;
            hoverTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(UiTheme.ButtonHover),
                "Chrome"));
            template.Triggers.Add(hoverTrigger);

            Trigger pressedTrigger = new Trigger();
            pressedTrigger.Property = ButtonBase.IsPressedProperty;
            pressedTrigger.Value = true;
            pressedTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(UiTheme.ButtonPressed),
                "Chrome"));
            template.Triggers.Add(pressedTrigger);

            // 放最后：禁用时压掉悬停/按下，避免深色下出现浅色块。
            Trigger disabledTrigger = new Trigger();
            disabledTrigger.Property = UIElement.IsEnabledProperty;
            disabledTrigger.Value = false;
            disabledTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(UiTheme.InputBackground),
                "Chrome"));
            disabledTrigger.Setters.Add(new Setter(
                Control.ForegroundProperty,
                new SolidColorBrush(UiTheme.TextDisabled)));
            disabledTrigger.Setters.Add(new Setter(
                Control.BorderBrushProperty,
                new SolidColorBrush(UiTheme.InputBorder)));
            template.Triggers.Add(disabledTrigger);

            return template;
        }

        private void StyleCheckBoxes()
        {
            PrepareCheckBox(_showTaskbarBox);
            PrepareCheckBox(_privacyBox);
            PrepareCheckBox(_claudeKeyBox);
            PrepareCheckBox(_hoverThemeBox);
            PrepareCheckBox(_externalEnabledBox);

            for (int index = 0; index < _segmentChecks.Count; index++)
            {
                PrepareCheckBox(_segmentChecks[index]);
            }
        }

        private static void PrepareCheckBox(CheckBox box)
        {
            if (box == null)
            {
                return;
            }

            box.Template = CreateCheckBoxTemplate();
            ApplyCheckTheme(box);
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            try
            {
                ApplyTheme();
            }
            catch (Exception exception)
            {
                Log.Write("设置窗口刷新主题失败：" + exception.GetType().Name);
            }
        }

        /// <summary>
        /// 让设置窗口整体跟随系统深浅色：底色、文字、输入框、下拉框、按钮
        /// 都从 UiTheme 取色，避免出现「深色底 + 系统白底输入框」的混搭。
        /// </summary>
        private void ApplyTheme()
        {
            Background = new SolidColorBrush(UiTheme.WindowBackground);
            Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);

            // 覆盖系统画刷：ComboBox 的下拉框、CheckBox 的方框、滚动条等由
            // 默认模板用 SystemColors 绘制，不改这些键就会出现「深色窗口里
            // 白底下浅灰字」的不可读组合。
            Resources[SystemColors.WindowBrushKey] =
                new SolidColorBrush(UiTheme.InputBackground);
            Resources[SystemColors.WindowTextBrushKey] =
                new SolidColorBrush(UiTheme.AppTextPrimary);
            Resources[SystemColors.ControlBrushKey] =
                new SolidColorBrush(UiTheme.ButtonSurface);
            Resources[SystemColors.ControlTextBrushKey] =
                new SolidColorBrush(UiTheme.AppTextPrimary);
            Resources[SystemColors.GrayTextBrushKey] =
                new SolidColorBrush(UiTheme.AppTextSecondary);
            Resources[SystemColors.HighlightBrushKey] =
                new SolidColorBrush(UiTheme.Accent);
            Resources[SystemColors.HighlightTextBrushKey] =
                new SolidColorBrush(UiTheme.AccentText);
            Resources[SystemColors.MenuBrushKey] =
                new SolidColorBrush(UiTheme.InputBackground);
            Resources[SystemColors.MenuTextBrushKey] =
                new SolidColorBrush(UiTheme.AppTextPrimary);

            // 侧边栏与导航项不走 ThemeElement（文字色取决于选中态，不能按字重推断）。
            if (_sidebarBorder != null)
            {
                _sidebarBorder.Background = new SolidColorBrush(UiTheme.SidebarBackground);
                _sidebarBorder.BorderBrush = new SolidColorBrush(UiTheme.CardBorder);
            }

            if (_brandText != null)
            {
                _brandText.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            }

            if (_brandSubtitle != null)
            {
                _brandSubtitle.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            }

            for (int index = 0; index < _navEntries.Count; index++)
            {
                ApplyNavState(_navEntries[index]);
            }

            if (_pageTitle != null)
            {
                _pageTitle.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            }

            // 卡片外壳单独上色，内部控件交给 ThemeElement 递归。
            for (int index = 0; index < _cards.Count; index++)
            {
                Border card = _cards[index];
                SettingsCard.ApplyTheme(card);
                ThemeElement(card);
            }

            // 操作栏不在卡片里（固定在滚动区之外），单独刷一遍。
            ThemeElement(_actionBarBorder);
            if (_actionBarBorder != null)
            {
                _actionBarBorder.BorderBrush = new SolidColorBrush(UiTheme.Separator);
            }

            if (_hoverPreview != null)
            {
                _hoverPreview.BorderBrush = new SolidColorBrush(UiTheme.CardBorder);
            }

            // 校验错误文案固定用 Critical 色，不能被 TextBlock 的通用配色覆盖。
            if (_validationText != null)
            {
                _validationText.Foreground =
                    new SolidColorBrush(UiTheme.RoleColor(ColorRole.Critical));
            }

            if (_commandEditor != null)
            {
                _commandEditor.ApplyTheme();
            }

            UpdateHoverPreview();
            ApplyWindowChromeTheme();
        }

        /// <summary>
        /// 让系统标题栏跟随应用深浅色。Win11 用 attribute 20，Win10 1809 及更早是 19；
        /// 系统不支持时 DWM 返回非 0（或根本没有 dwmapi），一律静默忽略，绝不影响窗口显示。
        /// </summary>
        private void ApplyWindowChromeTheme()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                {
                    // 还没走 SourceInitialized，等那次回调再设。
                    return;
                }

                int useDark = Theme.IsAppLight() ? 0 : 1;
                int result = NativeMethods.DwmSetWindowAttribute(
                    handle,
                    NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref useDark,
                    sizeof(int));

                if (result != 0)
                {
                    NativeMethods.DwmSetWindowAttribute(
                        handle,
                        NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY,
                        ref useDark,
                        sizeof(int));
                }
            }
            catch (Exception exception)
            {
                Log.Write("设置窗口标题栏配色失败：" + exception.GetType().Name);
            }
        }

        private void ThemeElement(DependencyObject element)
        {
            if (element == null)
            {
                return;
            }

            TextBlock text = element as TextBlock;
            if (text != null)
            {
                // 标题（AddLabel）用主色，提示（AddHint）用次级色。
                text.Foreground = new SolidColorBrush(
                    text.FontWeight == FontWeights.SemiBold
                        ? UiTheme.AppTextPrimary
                        : UiTheme.AppTextSecondary);
            }
            else if (element is TextBox || element is PasswordBox)
            {
                Control input = (Control)element;
                input.Background = new SolidColorBrush(UiTheme.InputBackground);
                input.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
                input.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);

                TextBox textBox = element as TextBox;
                if (textBox != null)
                {
                    textBox.CaretBrush = new SolidColorBrush(UiTheme.AppTextPrimary);
                }
            }
            else if (element is ComboBox)
            {
                ComboBox combo = (ComboBox)element;
                ApplyComboTheme(combo);

                for (int index = 0; index < combo.Items.Count; index++)
                {
                    ComboBoxItem item = combo.Items[index] as ComboBoxItem;
                    if (item != null)
                    {
                        item.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
                        item.Background = new SolidColorBrush(UiTheme.InputBackground);
                    }
                }
            }
            else if (element is CheckBox)
            {
                ApplyCheckTheme((CheckBox)element);
            }
            else if (element is Button)
            {
                // 默认 Button 模板在 IsEnabled=false 时用系统画刷，深色下会变成白块；
                // 统一换成代码构建的模板，按启用状态显式取色。
                Button button = (Button)element;
                button.Template = CreateButtonTemplate();
                ApplyButtonTheme(button);
            }
            else if (element is ListBox)
            {
                // ListBox 默认模板在深色下是白底黑字，必须显式上色。
                ListBox list = (ListBox)element;
                list.Background = new SolidColorBrush(UiTheme.InputBackground);
                list.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
                list.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
            }
            else if (element is ListBoxItem)
            {
                // 选中项的高亮由 ListBoxItem 模板的 Trigger 用 Accent 绘制。
                ListBoxItem item = (ListBoxItem)element;
                item.Background = new SolidColorBrush(UiTheme.InputBackground);
                item.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
                item.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
            }

            Panel panel = element as Panel;
            if (panel != null)
            {
                for (int index = 0; index < panel.Children.Count; index++)
                {
                    ThemeElement(panel.Children[index]);
                }

                return;
            }

            Decorator decorator = element as Decorator;
            if (decorator != null)
            {
                ThemeElement(decorator.Child);
                return;
            }

            ContentControl content = element as ContentControl;
            if (content != null)
            {
                ThemeElement(content.Content as DependencyObject);
            }
        }

        /// <summary>
        /// 左侧分类导航：品牌标题 + 4 个 pill 导航项。选中态用 NavSelected* 取色，
        /// 未选中用次要文字色 + 悬停底色。
        /// </summary>
        private FrameworkElement BuildSidebar()
        {
            StackPanel stack = new StackPanel();
            stack.Orientation = Orientation.Vertical;
            stack.Margin = new Thickness(0, 20, 0, 12);

            _brandText = new TextBlock();
            _brandText.Text = "VibeBar";
            _brandText.FontSize = 16;
            _brandText.FontWeight = FontWeights.SemiBold;
            _brandText.Margin = new Thickness(20, 0, 0, 2);
            _brandText.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            stack.Children.Add(_brandText);

            _brandSubtitle = new TextBlock();
            _brandSubtitle.Text = "设置";
            _brandSubtitle.FontSize = 12;
            _brandSubtitle.Margin = new Thickness(20, 0, 0, 16);
            _brandSubtitle.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            stack.Children.Add(_brandSubtitle);

            // 图标码位：账号 / 常规 / 扩展段 / 数据与集成（Segoe MDL2 Assets）。
            string[] icons = { "\uE77B", "\uE713", "\uE8F1", "\uE8B7" };
            for (int index = 0; index < icons.Length; index++)
            {
                stack.Children.Add(CreateNavItem(index, icons[index], PageTitles[index]));
            }

            _sidebarBorder = new Border();
            _sidebarBorder.Background = new SolidColorBrush(UiTheme.SidebarBackground);
            _sidebarBorder.BorderThickness = new Thickness(0, 0, 1, 0);
            _sidebarBorder.BorderBrush = new SolidColorBrush(UiTheme.CardBorder);
            _sidebarBorder.Child = stack;
            return _sidebarBorder;
        }

        private FrameworkElement CreateNavItem(int index, string icon, string title)
        {
            NavEntry entry = new NavEntry();
            entry.Index = index;

            TextBlock iconText = new TextBlock();
            iconText.Text = icon;
            iconText.FontFamily = SettingsCard.IconFont;
            iconText.FontSize = 16;
            iconText.VerticalAlignment = VerticalAlignment.Center;
            iconText.Width = 18;
            entry.Icon = iconText;

            TextBlock label = new TextBlock();
            label.Text = title;
            label.FontSize = 14;
            label.Margin = new Thickness(10, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            entry.Label = label;

            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(12, 0, 0, 0);
            row.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(iconText);
            row.Children.Add(label);

            Border pill = new Border();
            pill.Height = 40;
            pill.Margin = new Thickness(8, 2, 8, 2);
            pill.CornerRadius = new CornerRadius(6);
            pill.Background = Brushes.Transparent;
            pill.Cursor = Cursors.Hand;
            pill.Child = row;
            entry.Root = pill;

            // C# 5 的 for 循环变量会被闭包共享，先拷一份到局部变量。
            int pageIndex = index;
            pill.MouseLeftButtonUp += delegate { SelectPage(pageIndex); };
            pill.MouseEnter += delegate { ApplyNavHover(entry, true); };
            pill.MouseLeave += delegate { ApplyNavHover(entry, false); };

            _navEntries.Add(entry);
            return pill;
        }

        private void ApplyNavHover(NavEntry entry, bool hovering)
        {
            if (entry == null || entry.Root == null)
            {
                return;
            }

            if (entry.Selected)
            {
                entry.Root.Background = new SolidColorBrush(UiTheme.NavSelectedBackground);
                return;
            }

            entry.Root.Background = hovering
                ? new SolidColorBrush(UiTheme.NavHoverBackground)
                : Brushes.Transparent;
        }

        private void ApplyNavState(NavEntry entry)
        {
            if (entry == null || entry.Root == null)
            {
                return;
            }

            bool selected = entry.Index == _pageIndex;
            entry.Selected = selected;
            entry.Root.Background = selected
                ? new SolidColorBrush(UiTheme.NavSelectedBackground)
                : Brushes.Transparent;

            SolidColorBrush textBrush = new SolidColorBrush(
                selected ? UiTheme.NavSelectedText : UiTheme.AppTextSecondary);
            if (entry.Icon != null)
            {
                entry.Icon.Foreground = textBrush;
            }

            if (entry.Label != null)
            {
                entry.Label.Foreground = textBrush;
            }
        }

        /// <summary>
        /// 切换分类：只切 Visibility，不重建控件（重建会丢事件订阅与用户输入）。
        /// 每页有独立 ScrollViewer，切页时各自回到顶部。
        /// </summary>
        private void SelectPage(int index)
        {
            if (index < 0 || index >= _pageScrollers.Count)
            {
                return;
            }

            _pageIndex = index;
            for (int page = 0; page < _pageScrollers.Count; page++)
            {
                _pageScrollers[page].Visibility =
                    page == index ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_pageTitle != null)
            {
                _pageTitle.Text = PageTitles[index];
            }

            for (int nav = 0; nav < _navEntries.Count; nav++)
            {
                ApplyNavState(_navEntries[nav]);
            }

            _pageScrollers[index].ScrollToVerticalOffset(0);
        }

        /// <summary>内容区：页头标题 / 页面宿主（每页一个 ScrollViewer）/ 固定操作栏。</summary>
        private FrameworkElement BuildContentArea()
        {
            Grid area = new Grid();
            area.Margin = new Thickness(24, 20, 24, 12);

            RowDefinition headerRow = new RowDefinition();
            headerRow.Height = GridLength.Auto;
            RowDefinition bodyRow = new RowDefinition();
            bodyRow.Height = new GridLength(1, GridUnitType.Star);
            RowDefinition actionRow = new RowDefinition();
            actionRow.Height = GridLength.Auto;
            area.RowDefinitions.Add(headerRow);
            area.RowDefinitions.Add(bodyRow);
            area.RowDefinitions.Add(actionRow);

            _pageTitle = new TextBlock();
            _pageTitle.FontSize = 22;
            _pageTitle.FontWeight = FontWeights.SemiBold;
            _pageTitle.Margin = new Thickness(0, 0, 0, 16);
            _pageTitle.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            Grid.SetRow(_pageTitle, 0);
            area.Children.Add(_pageTitle);

            Grid pageHost = new Grid();
            Grid.SetRow(pageHost, 1);
            area.Children.Add(pageHost);

            AddPage(pageHost, BuildAccountPage());
            AddPage(pageHost, BuildGeneralPage());
            AddPage(pageHost, BuildExtensionPage());
            AddPage(pageHost, BuildDataPage());

            FrameworkElement actionBar = BuildActionBar();
            Grid.SetRow(actionBar, 2);
            area.Children.Add(actionBar);
            return area;
        }

        private void AddPage(Grid host, IList<Border> cards)
        {
            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Vertical;

            for (int index = 0; index < cards.Count; index++)
            {
                _cards.Add(cards[index]);
                panel.Children.Add(cards[index]);
            }

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Content = panel;
            scroll.Visibility = Visibility.Collapsed;
            host.Children.Add(scroll);
            _pageScrollers.Add(scroll);
        }

        private IList<Border> BuildAccountPage()
        {
            List<Border> cards = new List<Border>();

            // API Key：PasswordBox + 「从 Claude Code 读取」勾选（留空表示保持原 Key）。
            _apiKeyBox.Width = 360;
            _apiKeyBox.HorizontalAlignment = HorizontalAlignment.Left;
            _claudeKeyBox.Margin = new Thickness(0, 10, 0, 0);

            StackPanel keyContent = new StackPanel();
            keyContent.Orientation = Orientation.Vertical;
            keyContent.Children.Add(_apiKeyBox);
            keyContent.Children.Add(_claudeKeyBox);

            cards.Add(SettingsCard.CreateCustom(
                "\uE8D7",
                "API Key",
                "留空表示保持原 Key；Key 仅以 DPAPI 加密保存在本机，settings.json 中不含 Key。",
                keyContent));

            _baseUrlBox.Width = 320;
            _baseUrlBox.HorizontalAlignment = HorizontalAlignment.Left;
            cards.Add(SettingsCard.Create(
                "\uE774",
                "Base URL",
                "默认 https://api.deepseek.com；包含 /anthropic 时自动截断到域名根。",
                _baseUrlBox));

            _repoPathBox.Width = 320;
            _repoPathBox.HorizontalAlignment = HorizontalAlignment.Left;

            StackPanel repoRow = new StackPanel();
            repoRow.Orientation = Orientation.Horizontal;
            repoRow.Children.Add(_repoPathBox);

            Button browseButton = new Button();
            browseButton.Content = "浏览…";
            browseButton.Width = 76;
            browseButton.Height = 28;
            browseButton.Margin = new Thickness(8, 0, 0, 0);
            browseButton.VerticalAlignment = VerticalAlignment.Center;
            browseButton.Click += OnBrowse;
            repoRow.Children.Add(browseButton);

            cards.Add(SettingsCard.Create(
                "\uE8B7",
                "仓库路径",
                "Git 段读取的仓库目录。",
                repoRow));

            cards.Add(SettingsCard.Create(
                "\uE72C",
                "刷新间隔",
                "余额刷新最低 60 秒；选择 30 秒时按 60 秒保存。",
                _intervalBox));

            return cards;
        }

        private IList<Border> BuildGeneralPage()
        {
            List<Border> cards = new List<Border>();

            _showTaskbarBox.Margin = new Thickness(0);
            _privacyBox.Margin = new Thickness(0);

            List<FrameworkElement> displayRows = new List<FrameworkElement>();
            displayRows.Add(SettingsCard.CreateRow("任务栏", _showTaskbarBox));
            displayRows.Add(SettingsCard.CreateRow("隐私", _privacyBox));
            cards.Add(SettingsCard.CreateRows(
                "\uE7F4",
                "显示与隐私",
                "关闭任务栏显示后状态条隐藏，程序仍在托盘运行。",
                displayRows));

            List<FrameworkElement> moduleRows = new List<FrameworkElement>();
            for (int index = 0; index < _segmentChecks.Count; index++)
            {
                CheckBox box = _segmentChecks[index];
                string label = box.Content as string;
                // 名称交给左侧行标签，勾选框本身只留方框，避免同一行重复两遍。
                box.Content = string.Empty;
                box.Margin = new Thickness(0);
                moduleRows.Add(SettingsCard.CreateRow(
                    string.IsNullOrEmpty(label) ? "模块" : label,
                    box));
            }

            cards.Add(SettingsCard.CreateRows(
                "\uE71D",
                "显示模块",
                "至少保留一个模块；宽度不足时自动折叠为 +N。",
                moduleRows));

            List<FrameworkElement> areaRows = new List<FrameworkElement>();
            areaRows.Add(SettingsCard.CreateRow("吸附位置", _anchorBox));
            areaRows.Add(SettingsCard.CreateRow("显示器", _monitorBox));
            areaRows.Add(SettingsCard.CreateRow(_offsetLabel, _offsetBox));
            areaRows.Add(SettingsCard.CreateRow("最大宽度（px）", _maxWidthBox));
            cards.Add(SettingsCard.CreateRows(
                "\uE740",
                "任务栏显示区域",
                "靠右时偏移从托盘左缘算，靠左时从任务栏左缘算。",
                areaRows));

            _hoverThemeBox.Margin = new Thickness(0);

            StackPanel hoverContent = new StackPanel();
            hoverContent.Orientation = Orientation.Vertical;
            hoverContent.Children.Add(_hoverThemeBox);

            StackPanel hoverColorRow = new StackPanel();
            hoverColorRow.Orientation = Orientation.Horizontal;
            hoverColorRow.Margin = new Thickness(0, 10, 0, 0);
            hoverColorRow.Children.Add(CreateRowLabel("颜色"));
            hoverColorRow.Children.Add(_hoverColorBox);
            hoverColorRow.Children.Add(_hoverPreview);
            hoverContent.Children.Add(hoverColorRow);

            StackPanel hoverOpacityRow = new StackPanel();
            hoverOpacityRow.Orientation = Orientation.Horizontal;
            hoverOpacityRow.Margin = new Thickness(0, 10, 0, 0);
            hoverOpacityRow.Children.Add(CreateRowLabel("透明度"));
            hoverOpacityRow.Children.Add(_hoverOpacitySlider);
            hoverOpacityRow.Children.Add(_hoverOpacityLabel);
            hoverContent.Children.Add(hoverOpacityRow);

            hoverContent.Children.Add(CreateHint(
                "颜色留空或勾选「跟随系统主题色」时使用主题色；透明度 0% 表示悬停也不显示背景。"));

            cards.Add(SettingsCard.CreateCustom(
                "\uE790",
                "悬停背景",
                "鼠标悬停在任务栏状态条上时的背景色。",
                hoverContent));

            return cards;
        }

        private IList<Border> BuildExtensionPage()
        {
            List<Border> cards = new List<Border>();

            _portsBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            _portsBox.Margin = new Thickness(0);

            Grid portsRow = new Grid();
            ColumnDefinition portsColumn = new ColumnDefinition();
            portsColumn.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition resetColumn = new ColumnDefinition();
            resetColumn.Width = GridLength.Auto;
            portsRow.ColumnDefinitions.Add(portsColumn);
            portsRow.ColumnDefinitions.Add(resetColumn);

            Grid.SetColumn(_portsBox, 0);
            portsRow.Children.Add(_portsBox);

            Button resetPortsButton = new Button();
            resetPortsButton.Content = "恢复默认";
            resetPortsButton.Width = 86;
            resetPortsButton.Height = 28;
            resetPortsButton.Margin = new Thickness(8, 0, 0, 0);
            resetPortsButton.VerticalAlignment = VerticalAlignment.Top;
            resetPortsButton.Click += OnResetPorts;
            Grid.SetColumn(resetPortsButton, 1);
            portsRow.Children.Add(resetPortsButton);

            StackPanel portsContent = new StackPanel();
            portsContent.Orientation = Orientation.Vertical;
            portsContent.Children.Add(portsRow);
            portsContent.Children.Add(CreateHint("逗号/空格/换行分隔；留空表示显示全部监听端口。"));

            cards.Add(SettingsCard.CreateCustom(
                "\uE968",
                "关注端口",
                "只显示你关心的监听端口，其余忽略。",
                portsContent));

            cards.Add(SettingsCard.Create(
                "\uE72C",
                "端口刷新间隔",
                "端口段扫描本机监听端口的周期。",
                _portIntervalBox));

            cards.Add(SettingsCard.Create(
                "\uE8A5",
                "PR 刷新间隔",
                "GitHub PR 段的轮询周期。",
                _prIntervalBox));

            cards.Add(SettingsCard.CreateCustom(
                "\uE756",
                "自定义命令",
                "由你本人编写并在本机以 cmd 执行，效果等同于在命令行直接运行，"
                + "请只填写可信命令；超时固定 3 秒，失败只影响该段。",
                _commandEditor.Root));

            _externalEnabledBox.Content = string.Empty;
            _externalEnabledBox.Margin = new Thickness(0);

            List<FrameworkElement> externalRows = new List<FrameworkElement>();
            externalRows.Add(SettingsCard.CreateRow("启用外部段", _externalEnabledBox));
            externalRows.Add(SettingsCard.CreateRow(
                "最大段数",
                CreateUnitControl(_externalMaxSegmentsBox, "个")));
            externalRows.Add(SettingsCard.CreateRow(
                "默认存活",
                CreateUnitControl(_externalTtlBox, "秒")));
            externalRows.Add(CreateHint(
                "推送未带 ttl 时使用（5-3600 秒）。关闭「启用外部段」会立即清除当前所有外部段。"
                + "任何程序都可以通过命名管道 \\\\.\\pipe\\VibeBar.Segment.v1 推送段，"
                + "详见 docs/插件协议.md。"));

            cards.Add(SettingsCard.CreateRows(
                "\uE943",
                "外部段（插件）",
                "命令行用法 VibeBar.Cli.exe --segment id=... text=...；保存后立即生效，无需重启。",
                externalRows));

            return cards;
        }

        private IList<Border> BuildDataPage()
        {
            List<Border> cards = new List<Border>();

            TextBlock rules = new TextBlock();
            rules.Text = SegmentFactory.SettingsPeakRuleSummary;
            rules.TextWrapping = TextWrapping.Wrap;
            rules.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);

            cards.Add(SettingsCard.CreateCustom(
                "\uE9D9",
                "峰谷规则",
                "DeepSeek 计费时段（只读，随版本内置）。",
                rules));

            _hookStatusText.Margin = new Thickness(0);

            StackPanel hookContent = new StackPanel();
            hookContent.Orientation = Orientation.Vertical;
            hookContent.Children.Add(_hookStatusText);
            hookContent.Children.Add(CreateHint(
                "安装 Hooks + statusLine 到 %USERPROFILE%\\.claude\\settings.json，"
                + "安装前会自动备份；statusLine 用于显示模型与上下文占用。"
                + "对已启动的会话不生效，请启动新的 Claude Code 会话。"));

            StackPanel hookButtons = new StackPanel();
            hookButtons.Orientation = Orientation.Horizontal;
            hookButtons.Margin = new Thickness(0, 10, 0, 0);

            Button installHooksButton = new Button();
            installHooksButton.Content = "安装 Hooks + statusLine";
            installHooksButton.Width = 200;
            installHooksButton.Height = 30;
            installHooksButton.Click += OnInstallHooks;
            hookButtons.Children.Add(installHooksButton);

            Button uninstallHooksButton = new Button();
            uninstallHooksButton.Content = "卸载集成";
            uninstallHooksButton.Width = 100;
            uninstallHooksButton.Height = 30;
            uninstallHooksButton.Margin = new Thickness(8, 0, 0, 0);
            uninstallHooksButton.Click += OnUninstallHooks;
            hookButtons.Children.Add(uninstallHooksButton);

            hookContent.Children.Add(hookButtons);

            cards.Add(SettingsCard.CreateCustom(
                "\uE943",
                "Claude Code Hooks",
                "安装后由 Claude Code 会话把状态推给 VibeBar。",
                hookContent));

            StackPanel dataButtons = new StackPanel();
            dataButtons.Orientation = Orientation.Horizontal;

            Button openFolderButton = new Button();
            openFolderButton.Content = "打开数据目录";
            openFolderButton.Width = 108;
            openFolderButton.Height = 28;
            openFolderButton.Click += OnOpenDataFolder;
            dataButtons.Children.Add(openFolderButton);

            Button openConfigButton = new Button();
            openConfigButton.Content = "打开 settings.json";
            openConfigButton.Width = 150;
            openConfigButton.Height = 28;
            openConfigButton.Margin = new Thickness(8, 0, 0, 0);
            openConfigButton.Click += OnOpenSettingsFile;
            dataButtons.Children.Add(openConfigButton);

            cards.Add(SettingsCard.Create(
                "\uE838",
                "数据目录",
                "设置、密钥与日志都保存在 %APPDATA%\\VibeBar。",
                dataButtons));

            return cards;
        }

        private static TextBlock CreateHint(string text)
        {
            TextBlock hint = new TextBlock();
            hint.Text = text;
            hint.FontSize = 12;
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 8, 0, 0);
            hint.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            return hint;
        }

        /// <summary>卡片内的行标签：140 宽、次要色、垂直居中。</summary>
        private static TextBlock CreateRowLabel(string text)
        {
            TextBlock label = new TextBlock();
            label.Text = text;
            label.Width = SettingsCard.RowLabelWidth;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            return label;
        }

        /// <summary>「定宽输入框 + 单位」一行，单位文字显式上色（深色下否则是黑字）。</summary>
        private static StackPanel CreateUnitControl(TextBox box, string unitText)
        {
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Children.Add(box);

            TextBlock unit = new TextBlock();
            unit.Text = unitText;
            unit.Margin = new Thickness(6, 0, 0, 0);
            unit.VerticalAlignment = VerticalAlignment.Center;
            unit.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            row.Children.Add(unit);
            return row;
        }

        /// <summary>
        /// 内容区底部固定的操作栏：左侧校验红字 + 右侧保存/取消。
        /// 不放进 ScrollViewer，保证内容再长也能直接点到「保存」。
        /// </summary>
        private FrameworkElement BuildActionBar()
        {
            Grid bar = new Grid();

            ColumnDefinition infoColumn = new ColumnDefinition();
            infoColumn.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition buttonColumn = new ColumnDefinition();
            buttonColumn.Width = GridLength.Auto;
            bar.ColumnDefinitions.Add(infoColumn);
            bar.ColumnDefinitions.Add(buttonColumn);

            // 校验失败的红字显示在左侧；3 秒后或下次编辑时自动消失。
            _validationText.VerticalAlignment = VerticalAlignment.Center;
            _validationText.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(_validationText, 0);
            bar.Children.Add(_validationText);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;

            Button saveButton = new Button();
            saveButton.Content = "保存";
            saveButton.Width = 76;
            saveButton.Height = 30;
            saveButton.IsDefault = true;
            saveButton.Click += OnSave;
            buttons.Children.Add(saveButton);

            Button cancelButton = new Button();
            cancelButton.Content = "取消";
            cancelButton.Width = 76;
            cancelButton.Height = 30;
            cancelButton.IsCancel = true;
            cancelButton.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(cancelButton);

            Grid.SetColumn(buttons, 1);
            bar.Children.Add(buttons);

            _actionBarBorder = new Border();
            _actionBarBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            _actionBarBorder.BorderBrush = new SolidColorBrush(UiTheme.Separator);
            _actionBarBorder.Padding = new Thickness(0, 10, 0, 0);
            _actionBarBorder.Margin = new Thickness(0, 12, 0, 0);
            _actionBarBorder.Child = bar;
            return _actionBarBorder;
        }

        private void AddIntervalItem(int seconds)
        {
            ComboBoxItem item = new ComboBoxItem();
            item.Content = seconds.ToString() + " 秒";
            item.Tag = seconds;
            _intervalBox.Items.Add(item);
        }

        private void SelectInterval(int seconds)
        {
            if (seconds < 60)
            {
                seconds = 60;
            }
            else if (seconds > 3600)
            {
                seconds = 3600;
            }

            for (int index = 0; index < _intervalBox.Items.Count; index++)
            {
                ComboBoxItem item = _intervalBox.Items[index] as ComboBoxItem;
                if (item == null || !(item.Tag is int))
                {
                    continue;
                }

                if ((int)item.Tag == seconds)
                {
                    _intervalBox.SelectedIndex = index;
                    return;
                }
            }

            // settings.json 手工配置了列表外的合法值时保留它，避免保存时被悄悄改掉。
            AddIntervalItem(seconds);
            _intervalBox.SelectedIndex = _intervalBox.Items.Count - 1;
        }

        private int GetSelectedInterval()
        {
            ComboBoxItem item = _intervalBox.SelectedItem as ComboBoxItem;
            if (item != null && item.Tag is int)
            {
                return (int)item.Tag;
            }

            return 60;
        }

        /// <summary>
        /// 构建「N 秒」下拉框。settings.json 里手工写了列表外的值时补一项，
        /// 避免只因为打开设置窗口就被悄悄改成候选值。
        /// </summary>
        private static ComboBox CreateSecondsCombo(int[] choices, int current, int fallback)
        {
            if (current <= 0)
            {
                current = fallback;
            }

            ComboBox combo = new ComboBox();
            combo.Height = 30;
            combo.Width = 160;
            combo.HorizontalAlignment = HorizontalAlignment.Left;
            combo.Template = CreateComboBoxTemplate();

            bool matched = false;
            for (int index = 0; index < choices.Length; index++)
            {
                AddSecondsItem(combo, choices[index]);
                if (choices[index] == current)
                {
                    matched = true;
                }
            }

            if (!matched)
            {
                AddSecondsItem(combo, current);
            }

            SelectSeconds(combo, current);
            return combo;
        }

        private static void AddSecondsItem(ComboBox combo, int seconds)
        {
            ComboBoxItem item = new ComboBoxItem();
            item.Content = seconds.ToString(CultureInfo.InvariantCulture) + " 秒";
            item.Tag = seconds;
            combo.Items.Add(item);
        }

        private static void SelectSeconds(ComboBox combo, int seconds)
        {
            for (int index = 0; index < combo.Items.Count; index++)
            {
                ComboBoxItem item = combo.Items[index] as ComboBoxItem;
                if (item != null && item.Tag is int && (int)item.Tag == seconds)
                {
                    combo.SelectedIndex = index;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private static int GetSelectedSeconds(ComboBox combo, int fallback)
        {
            ComboBoxItem item = combo == null ? null : combo.SelectedItem as ComboBoxItem;
            if (item != null && item.Tag is int)
            {
                return (int)item.Tag;
            }

            return fallback;
        }

        private void OnResetPorts(object sender, RoutedEventArgs e)
        {
            _portsBox.Text = SettingsValidation.FormatPorts(
                PortsSettings.CreateDefaultWatchPorts());
            ClearValidationError();
        }

        private void ShowValidationError(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return;
            }

            _validationText.Text = error;
            _validationText.Visibility = Visibility.Visible;
            _validationTimer.Stop();
            _validationTimer.Start();
        }

        private void ClearValidationError()
        {
            if (_validationTimer != null)
            {
                _validationTimer.Stop();
            }

            if (_validationText == null)
            {
                return;
            }

            _validationText.Text = string.Empty;
            _validationText.Visibility = Visibility.Collapsed;
        }

        private void SelectMonitor(string monitor)
        {
            string target = string.Equals(
                monitor,
                "secondary",
                StringComparison.OrdinalIgnoreCase)
                ? "secondary"
                : "primary";

            for (int index = 0; index < _monitorBox.Items.Count; index++)
            {
                ComboBoxItem item = _monitorBox.Items[index] as ComboBoxItem;
                if (item != null
                    && string.Equals(item.Tag as string, target, StringComparison.Ordinal))
                {
                    _monitorBox.SelectedIndex = index;
                    return;
                }
            }

            _monitorBox.SelectedIndex = 0;
        }

        private string GetSelectedMonitor()
        {
            ComboBoxItem item = _monitorBox.SelectedItem as ComboBoxItem;
            string monitor = item == null ? null : item.Tag as string;
            return string.Equals(monitor, "secondary", StringComparison.Ordinal)
                ? "secondary"
                : "primary";
        }

        private void SelectAnchor(string anchor)
        {
            string target = string.Equals(
                anchor,
                "left",
                StringComparison.OrdinalIgnoreCase)
                ? "left"
                : "right";

            for (int index = 0; index < _anchorBox.Items.Count; index++)
            {
                ComboBoxItem item = _anchorBox.Items[index] as ComboBoxItem;
                if (item != null
                    && string.Equals(item.Tag as string, target, StringComparison.Ordinal))
                {
                    _anchorBox.SelectedIndex = index;
                    return;
                }
            }

            _anchorBox.SelectedIndex = 0;
        }

        private string GetSelectedAnchor()
        {
            ComboBoxItem item = _anchorBox.SelectedItem as ComboBoxItem;
            string anchor = item == null ? null : item.Tag as string;
            return string.Equals(anchor, "left", StringComparison.Ordinal)
                ? "left"
                : "right";
        }

        private void UpdateOffsetLabel()
        {
            if (_offsetLabel == null)
            {
                return;
            }

            _offsetLabel.Text = string.Equals(
                GetSelectedAnchor(),
                "left",
                StringComparison.Ordinal)
                ? "左侧偏移（px）"
                : "托盘偏移（px）";
        }

        /// <summary>
        /// 未勾选「启用外部段」时两个数值框禁用（灰化）：此时保存也不会用到它们，
        /// 但保留原文本，重新勾选即可恢复。
        /// </summary>
        private void UpdateExternalControls()
        {
            if (_externalMaxSegmentsBox == null || _externalTtlBox == null)
            {
                return;
            }

            bool enabled = _externalEnabledBox != null && _externalEnabledBox.IsChecked == true;
            _externalMaxSegmentsBox.IsEnabled = enabled;
            _externalTtlBox.IsEnabled = enabled;
        }

        private void UpdateHoverControls()
        {
            bool followTheme = _hoverThemeBox.IsChecked == true;
            _hoverColorBox.IsEnabled = !followTheme;
            if (followTheme)
            {
                _hoverColorBox.Text = string.Empty;
            }

            int percent = (int)Math.Round(_hoverOpacitySlider.Value);
            _hoverOpacityLabel.Text = percent.ToString(
                CultureInfo.InvariantCulture) + "%";
        }

        private void UpdateHoverPreview()
        {
            if (_hoverPreview == null)
            {
                return;
            }

            Color baseColor;
            if (_hoverThemeBox.IsChecked == true)
            {
                Color theme = UiTheme.BarBackground;
                baseColor = Color.FromRgb(theme.R, theme.G, theme.B);
            }
            else
            {
                string normalized = UiSettings.NormalizeColor(_hoverColorBox.Text);
                baseColor = ParseColorOrTheme(normalized);
            }

            int alpha = (int)Math.Round(_hoverOpacitySlider.Value * 255.0 / 100.0);
            if (alpha < 0)
            {
                alpha = 0;
            }
            else if (alpha > 255)
            {
                alpha = 255;
            }

            _hoverPreview.Background = new SolidColorBrush(
                Color.FromArgb((byte)alpha, baseColor.R, baseColor.G, baseColor.B));
        }

        private static Color ParseColorOrTheme(string normalized)
        {
            if (string.IsNullOrEmpty(normalized) || normalized.Length != 7)
            {
                Color theme = UiTheme.BarBackground;
                return Color.FromRgb(theme.R, theme.G, theme.B);
            }

            int red;
            int green;
            int blue;
            if (int.TryParse(
                    normalized.Substring(1, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out red)
                && int.TryParse(
                    normalized.Substring(3, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out green)
                && int.TryParse(
                    normalized.Substring(5, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out blue))
            {
                return Color.FromRgb((byte)red, (byte)green, (byte)blue);
            }

            Color fallback = UiTheme.BarBackground;
            return Color.FromRgb(fallback.R, fallback.G, fallback.B);
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择 Git 仓库目录";
                dialog.ShowNewFolderButton = true;

                string current = _repoPathBox.Text.Trim();
                if (current.Length > 0 && Directory.Exists(current))
                {
                    dialog.SelectedPath = current;
                }

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    _repoPathBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
        {
            try
            {
                // 首次运行可能还没有 settings.json，先写一次再打开。
                if (!File.Exists(_settingsStore.FilePath))
                {
                    _settingsStore.Save(_settings);
                }

                // 交给 shell 用系统默认关联打开。不要硬编码 notepad.exe：
                // Win11 的记事本是打包应用，由普通进程直接 CreateProcess
                // 且环境块被裁剪时会报「找不到 Microsoft.UI.Windowing.Core.dll」。
                ProcessStartInfo info = new ProcessStartInfo(_settingsStore.FilePath);
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch (Exception exception)
            {
                Log.Error("打开 settings.json 失败", exception);

                try
                {
                    Process.Start(
                        "explorer.exe",
                        "/select,\"" + _settingsStore.FilePath + "\"");
                    return;
                }
                catch
                {
                    // 继续走提示分支。
                }

                MessageBox.Show(
                    this,
                    "无法打开 settings.json，可在数据目录中手工编辑。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void OnOpenDataFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VibeBar");
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception exception)
            {
                Log.Write("打开数据目录失败：" + exception.GetType().Name);
                MessageBox.Show(
                    this,
                    "无法打开数据目录。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void RefreshHookStatus()
        {
            bool installed = false;
            bool statusLineInstalled = false;
            try
            {
                installed = _hookInstaller.IsInstalled();
                statusLineInstalled = _hookInstaller.IsStatusLineInstalled();
            }
            catch (Exception exception)
            {
                Log.Write("检查 Hooks 安装状态失败：" + exception.GetType().Name);
            }

            _settings.Hooks.Installed = installed;
            _settings.Hooks.StatusLineInstalled = statusLineInstalled;

            if (installed && statusLineInstalled)
            {
                _hookStatusText.Text = "状态：已安装 Hooks + statusLine（对新会话生效）";
            }
            else if (installed)
            {
                _hookStatusText.Text = "状态：Hooks 已安装，statusLine 未安装";
            }
            else if (statusLineInstalled)
            {
                _hookStatusText.Text = "状态：statusLine 已安装，Hooks 未安装";
            }
            else
            {
                _hookStatusText.Text = "状态：未安装";
            }
        }

        private void OnInstallHooks(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                this,
                "将安装 Hooks 与 statusLine（模型/上下文显示）。"
                + "安装前会把 settings.json 备份到同目录。"
                + "安装后请启动新的 Claude Code 会话才会生效。\n\n是否继续？",
                "安装 Claude Code 集成",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }

            string error;
            try
            {
                error = _hookInstaller.Install();
            }
            catch (Exception exception)
            {
                Log.Error("安装 Claude Code Hooks 失败", exception);
                error = "安装 Hooks 失败，详见日志";
            }

            if (!string.IsNullOrEmpty(error))
            {
                MessageBox.Show(
                    this,
                    error,
                    "安装 Claude Code Hooks",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _settings.Hooks.Installed = true;
            _settings.Hooks.StatusLineInstalled = true;
            SaveHookState();
            RefreshHookStatus();

            MessageBox.Show(
                this,
                "Hooks 与 statusLine 已安装。请启动新的 Claude Code 会话后生效。",
                "安装 Claude Code 集成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OnUninstallHooks(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                this,
                "将移除 VibeBar 的 Hooks 与 statusLine，其他配置与插件 hooks 不受影响。\n\n是否继续？",
                "卸载 Claude Code 集成",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }

            string error;
            try
            {
                error = _hookInstaller.Uninstall();
            }
            catch (Exception exception)
            {
                Log.Error("卸载 Claude Code Hooks 失败", exception);
                error = "卸载 Hooks 失败，详见日志";
            }

            if (!string.IsNullOrEmpty(error))
            {
                MessageBox.Show(
                    this,
                    error,
                    "卸载 Hooks",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _settings.Hooks.Installed = false;
            _settings.Hooks.StatusLineInstalled = false;
            SaveHookState();
            RefreshHookStatus();

            MessageBox.Show(
                this,
                "Hooks 与 statusLine 已卸载。",
                "卸载 Claude Code 集成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void SaveHookState()
        {
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception exception)
            {
                Log.Error("保存 Hooks 状态失败", exception);
                MessageBox.Show(
                    this,
                    "Hooks 状态已更新，但保存 settings.json 失败，详见日志。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            ClearValidationError();

            // 扩展段：先校验关注端口与自定义命令，失败只在窗口内显示红字、不关闭窗口。
            int[] ports;
            string error;
            if (!SettingsValidation.TryParsePorts(_portsBox.Text, out ports, out error))
            {
                ShowValidationError(error);
                return;
            }

            if (!_commandEditor.TryCommit(out error))
            {
                ShowValidationError(error);
                return;
            }

            int externalMaxSegments;
            if (!SettingsValidation.TryParseExternalMaxSegments(
                _externalMaxSegmentsBox.Text,
                out externalMaxSegments,
                out error))
            {
                ShowValidationError(error);
                if (_externalMaxSegmentsBox.IsEnabled)
                {
                    _externalMaxSegmentsBox.Focus();
                }

                return;
            }

            int externalTtlSeconds;
            if (!SettingsValidation.TryParseExternalTtl(
                _externalTtlBox.Text,
                out externalTtlSeconds,
                out error))
            {
                ShowValidationError(error);
                if (_externalTtlBox.IsEnabled)
                {
                    _externalTtlBox.Focus();
                }

                return;
            }

            _settings.Ports.WatchPorts = ports;
            _settings.Ports.PollSeconds = GetSelectedSeconds(_portIntervalBox, 5);
            _settings.Pr.PollSeconds = GetSelectedSeconds(_prIntervalBox, 60);

            // 外部段：enabled=false 时 AppController 会停管道并清掉现有外部段。
            _settings.External.Enabled = _externalEnabledBox.IsChecked == true;
            _settings.External.MaxSegments = externalMaxSegments;
            _settings.External.DefaultTtlSeconds = externalTtlSeconds;

            string baseUrlText = _baseUrlBox.Text.Trim();
            Uri uri;
            if (baseUrlText.Length == 0
                || !Uri.TryCreate(baseUrlText, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttp
                    && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(
                    this,
                    "Base URL 必须是有效的 http/https 地址。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _baseUrlBox.Focus();
                return;
            }

            string key = _apiKeyBox.Password;
            if (!string.IsNullOrEmpty(key))
            {
                key = key.Trim();
                if (key.Length > 0)
                {
                    if (!Secrets.SaveApiKey(key))
                    {
                        MessageBox.Show(
                            this,
                            "保存 API Key 失败，详见日志。",
                            "VibeBar",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            _settings.Quota.BaseUrl =
                DeepSeekQuotaProvider.NormalizeBaseUrl(baseUrlText);
            _settings.Quota.ReadKeyFromClaudeSettings =
                _claudeKeyBox.IsChecked == true;
            _settings.Git.RepoPath = _repoPathBox.Text.Trim();

            int refreshSeconds = GetSelectedInterval();
            if (refreshSeconds < 60)
            {
                refreshSeconds = 60;
            }
            else if (refreshSeconds > 3600)
            {
                refreshSeconds = 3600;
            }

            _settings.Quota.RefreshSeconds = refreshSeconds;
            _settings.General.ShowTaskbar = _showTaskbarBox.IsChecked == true;
            _settings.General.PrivacyMode = _privacyBox.IsChecked == true;

            int offsetX;
            if (!int.TryParse(
                _offsetBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out offsetX))
            {
                MessageBox.Show(
                    this,
                    "距托盘偏移必须是整数（0–400）。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _offsetBox.Focus();
                return;
            }

            int maxWidth;
            if (!int.TryParse(
                _maxWidthBox.Text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out maxWidth))
            {
                MessageBox.Show(
                    this,
                    "最大宽度必须是整数（120–800）。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _maxWidthBox.Focus();
                return;
            }

            bool anySegmentEnabled = false;
            for (int index = 0; index < _segmentChecks.Count; index++)
            {
                if (_segmentChecks[index].IsChecked == true)
                {
                    anySegmentEnabled = true;
                    break;
                }
            }

            if (!anySegmentEnabled)
            {
                MessageBox.Show(
                    this,
                    "至少保留一个显示模块。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string hoverColor = _hoverThemeBox.IsChecked == true
                ? string.Empty
                : UiSettings.NormalizeColor(_hoverColorBox.Text);
            if (_hoverThemeBox.IsChecked != true && hoverColor.Length == 0)
            {
                MessageBox.Show(
                    this,
                    "悬停颜色格式应为 #RRGGBB，例如 #FF8800；或勾选「跟随系统主题色」。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _hoverColorBox.Focus();
                return;
            }

            _settings.Ui.Anchor = GetSelectedAnchor();
            _settings.Ui.Monitor = GetSelectedMonitor();
            _settings.Ui.OffsetX = offsetX;
            _settings.Ui.MaxWidth = maxWidth;
            for (int index = 0; index < _segmentChecks.Count; index++)
            {
                CheckBox box = _segmentChecks[index];
                string id = box.Tag as string;
                if (!string.IsNullOrEmpty(id))
                {
                    _settings.Ui.SetSegmentEnabled(id, box.IsChecked == true);
                }
            }

            _settings.Ui.HoverColor = hoverColor;
            _settings.Ui.HoverOpacity = (int)Math.Round(
                _hoverOpacitySlider.Value * 255.0 / 100.0);
            UiSettings.Normalize(_settings.Ui);

            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception exception)
            {
                Log.Error("保存设置失败", exception);
                MessageBox.Show(
                    this,
                    "保存设置失败，详见日志。",
                    "VibeBar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            EventHandler handler = Saved;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }

            try
            {
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                Close();
            }
        }
    }
}
