using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
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
        private StackPanel _rootPanel;
        private Drawing.Rectangle _targetWork = Drawing.Rectangle.Empty;
        private bool _placementHooked;

        internal event EventHandler Saved;

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
            Width = 520;
            SizeToContent = SizeToContent.Height;
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

            StyleCheckBoxes();
            UpdateHoverControls();
            UpdateHoverPreview();

            _hookStatusText = new TextBlock();
            _hookStatusText.TextWrapping = TextWrapping.Wrap;
            _hookStatusText.Margin = new Thickness(0, 4, 0, 0);
            _hookStatusText.Opacity = 0.85;
            RefreshHookStatus();

            ScrollViewer scroll = new ScrollViewer();
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroll.Content = BuildLayout();
            scroll.MaxHeight = Math.Max(
                360.0,
                SystemParameters.WorkArea.Height - 120.0);
            Content = scroll;

            // 主题可能在设置窗口打开期间切换（系统深浅色），统一在窗口级重刷。
            Theme.Changed += OnThemeChanged;
            Closed += delegate { Theme.Changed -= OnThemeChanged; };
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
        /// </summary>
        private ControlTemplate CreateComboBoxTemplate()
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
        private static ControlTemplate CreateCheckBoxTemplate()
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
            content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
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

        private static void ApplyCheckTheme(CheckBox box)
        {
            box.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            box.Background = new SolidColorBrush(UiTheme.InputBackground);
            box.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
        }

        private void StyleCheckBoxes()
        {
            PrepareCheckBox(_showTaskbarBox);
            PrepareCheckBox(_privacyBox);
            PrepareCheckBox(_claudeKeyBox);
            PrepareCheckBox(_hoverThemeBox);

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

            ThemeElement(_rootPanel);
            UpdateHoverPreview();
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
                Control button = (Control)element;
                button.Background = new SolidColorBrush(UiTheme.ButtonSurface);
                button.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
                button.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
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

        private UIElement BuildLayout()
        {
            StackPanel root = new StackPanel();
            root.Margin = new Thickness(18);
            _rootPanel = root;

            AddLabel(root, "API Key");
            root.Children.Add(_apiKeyBox);
            AddHint(root, "留空表示保持原 Key；Key 仅以 DPAPI 加密保存在本机。");

            AddLabel(root, "Base URL");
            root.Children.Add(_baseUrlBox);
            AddHint(root, "默认 https://api.deepseek.com；包含 /anthropic 时自动截断到域名根。");

            AddLabel(root, "仓库路径");
            Grid repoRow = new Grid();
            ColumnDefinition textColumn = new ColumnDefinition();
            textColumn.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition buttonColumn = new ColumnDefinition();
            buttonColumn.Width = GridLength.Auto;
            repoRow.ColumnDefinitions.Add(textColumn);
            repoRow.ColumnDefinitions.Add(buttonColumn);

            Grid.SetColumn(_repoPathBox, 0);
            repoRow.Children.Add(_repoPathBox);

            Button browseButton = new Button();
            browseButton.Content = "浏览…";
            browseButton.Width = 76;
            browseButton.Height = 28;
            browseButton.Margin = new Thickness(8, 0, 0, 0);
            browseButton.Click += OnBrowse;
            Grid.SetColumn(browseButton, 1);
            repoRow.Children.Add(browseButton);

            root.Children.Add(repoRow);

            AddLabel(root, "刷新间隔");
            root.Children.Add(_intervalBox);
            AddHint(root, "余额刷新最低 60 秒；选择 30 秒时按 60 秒保存。");

            AddLabel(root, "显示与隐私");
            root.Children.Add(_showTaskbarBox);
            root.Children.Add(_privacyBox);
            root.Children.Add(_claudeKeyBox);

            AddLabel(root, "任务栏显示区域");
            root.Children.Add(CreateInlineRow("吸附方式", _anchorBox));
            root.Children.Add(CreateInlineRow("显示器", _monitorBox));

            StackPanel offsetRow = new StackPanel();
            offsetRow.Orientation = Orientation.Horizontal;
            offsetRow.Margin = new Thickness(0, 6, 0, 0);
            offsetRow.Children.Add(_offsetLabel);
            offsetRow.Children.Add(_offsetBox);
            root.Children.Add(offsetRow);

            root.Children.Add(CreateInlineRow("最大宽度（px）", _maxWidthBox));

            StackPanel moduleRow = new StackPanel();
            moduleRow.Orientation = Orientation.Horizontal;
            moduleRow.Margin = new Thickness(0, 8, 0, 0);
            TextBlock moduleLabel = new TextBlock();
            moduleLabel.Text = "显示模块";
            moduleLabel.Width = 150;
            moduleLabel.VerticalAlignment = VerticalAlignment.Center;
            moduleRow.Children.Add(moduleLabel);

            // 段数量会随插件式扩展增长（额度/Git/会话/端口/PR/自定义），
            // 用 WrapPanel 换行，避免像横向 StackPanel 那样被窗口右缘裁掉。
            WrapPanel segmentRow = new WrapPanel();
            segmentRow.Orientation = Orientation.Horizontal;
            segmentRow.MaxWidth = 300;
            for (int index = 0; index < _segmentChecks.Count; index++)
            {
                segmentRow.Children.Add(_segmentChecks[index]);
            }

            moduleRow.Children.Add(segmentRow);
            root.Children.Add(moduleRow);
            AddHint(root, "靠右时偏移从托盘左缘算，靠左时从任务栏左缘算；宽度不足时自动折叠为 +N。至少保留一个模块。");

            AddLabel(root, "悬停背景");
            root.Children.Add(_hoverThemeBox);

            StackPanel hoverColorRow = new StackPanel();
            hoverColorRow.Orientation = Orientation.Horizontal;
            hoverColorRow.Margin = new Thickness(0, 8, 0, 0);
            TextBlock colorLabel = new TextBlock();
            colorLabel.Text = "颜色";
            colorLabel.Width = 150;
            colorLabel.VerticalAlignment = VerticalAlignment.Center;
            hoverColorRow.Children.Add(colorLabel);
            hoverColorRow.Children.Add(_hoverColorBox);
            hoverColorRow.Children.Add(_hoverPreview);
            root.Children.Add(hoverColorRow);

            StackPanel hoverOpacityRow = new StackPanel();
            hoverOpacityRow.Orientation = Orientation.Horizontal;
            hoverOpacityRow.Margin = new Thickness(0, 8, 0, 0);
            TextBlock opacityLabel = new TextBlock();
            opacityLabel.Text = "透明度";
            opacityLabel.Width = 150;
            opacityLabel.VerticalAlignment = VerticalAlignment.Center;
            hoverOpacityRow.Children.Add(opacityLabel);
            hoverOpacityRow.Children.Add(_hoverOpacitySlider);
            hoverOpacityRow.Children.Add(_hoverOpacityLabel);
            root.Children.Add(hoverOpacityRow);
            AddHint(root, "颜色留空或勾选「跟随系统主题色」时使用主题色；透明度 0% 表示悬停也不显示背景。");

            AddLabel(root, "扩展段（端口 / PR / 自定义命令）");
            AddHint(
                root,
                "端口段与 PR 段默认关闭，可在上方「显示模块」中开启。"
                + "关注端口 ports.watchPorts、PR 刷新间隔 pr.pollSeconds、"
                + "自定义命令 custom.commands 都在 settings.json 中配置；"
                + "修改后需重启 VibeBar（不要在保存设置前手工改文件）。");
            AddHint(
                root,
                "⚠ 自定义命令由你本人编写并在本机以 cmd 执行，效果等同于在命令行直接运行，"
                + "请只填写可信命令；超时固定 3 秒，失败只影响该段。");

            Button openConfigButton = new Button();
            openConfigButton.Content = "打开 settings.json";
            openConfigButton.Width = 150;
            openConfigButton.Height = 28;
            openConfigButton.HorizontalAlignment = HorizontalAlignment.Left;
            openConfigButton.Margin = new Thickness(0, 8, 0, 0);
            openConfigButton.Click += OnOpenSettingsFile;
            root.Children.Add(openConfigButton);

            AddLabel(root, "峰谷规则（只读）");
            TextBlock rules = new TextBlock();
            rules.Text = SegmentFactory.SettingsPeakRuleSummary;
            rules.TextWrapping = TextWrapping.Wrap;
            rules.Opacity = 0.85;
            rules.Margin = new Thickness(0, 2, 0, 0);
            root.Children.Add(rules);

            AddLabel(root, "Claude Code Hooks");
            root.Children.Add(_hookStatusText);
            AddHint(
                root,
                "安装 Hooks + statusLine 到 %USERPROFILE%\\.claude\\settings.json，"
                + "安装前会自动备份；statusLine 用于显示模型与上下文占用。"
                + "对已启动的会话不生效，请启动新的 Claude Code 会话。");

            StackPanel hookButtons = new StackPanel();
            hookButtons.Orientation = Orientation.Horizontal;
            hookButtons.Margin = new Thickness(0, 8, 0, 0);

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

            root.Children.Add(hookButtons);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.Margin = new Thickness(0, 20, 0, 0);

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

            Button openFolderButton = new Button();
            openFolderButton.Content = "打开数据目录";
            openFolderButton.Width = 108;
            openFolderButton.Height = 30;
            openFolderButton.Margin = new Thickness(16, 0, 0, 0);
            openFolderButton.Click += OnOpenDataFolder;
            buttons.Children.Add(openFolderButton);

            root.Children.Add(buttons);
            return root;
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
                ? "距任务栏左侧偏移（px）"
                : "距托盘偏移（px）";
        }

        private static StackPanel CreateInlineRow(string labelText, UIElement control)
        {
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 6, 0, 0);

            TextBlock label = new TextBlock();
            label.Text = labelText;
            label.Width = 150;
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            row.Children.Add(control);
            return row;
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

        private static void AddLabel(Panel panel, string text)
        {
            TextBlock label = new TextBlock();
            label.Text = text;
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(0, 12, 0, 4);
            panel.Children.Add(label);
        }

        private static void AddHint(Panel panel, string text)
        {
            TextBlock hint = new TextBlock();
            hint.Text = text;
            hint.FontSize = 12;
            hint.Opacity = 0.75;
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(hint);
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
