using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VibeBar
{
    /// <summary>
    /// 自定义命令列表编辑器（代码构建，无 XAML）。构造时把 source 深拷贝成工作副本，
    /// 字段编辑只写工作副本；只有 TryCommit 校验通过才写回 source，
    /// 因此「取消」或校验失败都不会改动已保存的配置。
    /// </summary>
    internal sealed class CustomCommandEditor
    {
        private readonly IList<CustomCommandSettings> _source;
        private readonly List<CustomCommandSettings> _commands = new List<CustomCommandSettings>();

        private readonly StackPanel _root;
        private readonly ListBox _list;
        private readonly Button _addButton;
        private readonly Button _deleteButton;
        private readonly StackPanel _fieldPanel;
        private readonly TextBox _labelBox;
        private readonly TextBox _commandBox;
        private readonly TextBox _intervalBox;
        private readonly ComboBox _formatBox;
        private readonly TextBox _urlBox;
        private readonly CheckBox _enabledBox;

        // ListBoxItem 模板只依赖主题色，构建一次后所有项共用。
        private readonly ControlTemplate _itemTemplate;

        // 加载字段/重建列表期间抑制写回，避免把「读取选中项」误当成「用户编辑」。
        private bool _loading;

        internal CustomCommandEditor(IList<CustomCommandSettings> source)
        {
            _source = source;
            if (source != null)
            {
                for (int index = 0; index < source.Count; index++)
                {
                    _commands.Add(Clone(source[index]));
                }
            }

            _root = new StackPanel();
            _root.Margin = new Thickness(0, 4, 0, 0);

            _itemTemplate = CreateListBoxItemTemplate();

            _list = new ListBox();
            _list.Height = 110;
            _list.Margin = new Thickness(0, 0, 0, 0);
            _list.SelectionChanged += OnSelectionChanged;
            _root.Children.Add(_list);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.Margin = new Thickness(0, 6, 0, 0);

            _addButton = new Button();
            _addButton.Content = "添加";
            _addButton.Width = 76;
            _addButton.Height = 28;
            _addButton.Click += OnAdd;
            buttons.Children.Add(_addButton);

            _deleteButton = new Button();
            _deleteButton.Content = "删除";
            _deleteButton.Width = 76;
            _deleteButton.Height = 28;
            _deleteButton.Margin = new Thickness(8, 0, 0, 0);
            _deleteButton.IsEnabled = false;
            _deleteButton.Click += OnDelete;
            buttons.Children.Add(_deleteButton);

            _root.Children.Add(buttons);

            _fieldPanel = new StackPanel();
            _fieldPanel.Margin = new Thickness(0, 8, 0, 0);
            _fieldPanel.IsEnabled = false;

            _labelBox = CreateTextBox(200);
            _labelBox.TextChanged += OnTextChanged;
            _fieldPanel.Children.Add(CreateFieldRow("标签", _labelBox));

            _commandBox = CreateTextBox(380);
            _commandBox.TextChanged += OnTextChanged;
            _fieldPanel.Children.Add(CreateFieldRow("命令", _commandBox));

            _intervalBox = CreateTextBox(90);
            _intervalBox.TextChanged += OnTextChanged;

            StackPanel intervalRow = new StackPanel();
            intervalRow.Orientation = Orientation.Horizontal;
            intervalRow.Margin = new Thickness(0, 6, 0, 0);
            intervalRow.Children.Add(CreateFieldLabel("间隔(秒)", 60));
            intervalRow.Children.Add(_intervalBox);

            _formatBox = new ComboBox();
            _formatBox.Height = 28;
            _formatBox.Width = 100;
            _formatBox.VerticalAlignment = VerticalAlignment.Center;
            _formatBox.Margin = new Thickness(10, 0, 0, 0);
            _formatBox.Template = SettingsWindow.CreateComboBoxTemplate();

            ComboBoxItem textItem = new ComboBoxItem();
            textItem.Content = "text";
            textItem.Tag = "text";
            _formatBox.Items.Add(textItem);

            ComboBoxItem jsonItem = new ComboBoxItem();
            jsonItem.Content = "json";
            jsonItem.Tag = "json";
            _formatBox.Items.Add(jsonItem);

            _formatBox.SelectedIndex = 0;
            _formatBox.SelectionChanged += OnFormatChanged;

            TextBlock formatLabel = CreateFieldLabel("格式", 40);
            formatLabel.Margin = new Thickness(0, 0, 0, 0);
            intervalRow.Children.Add(formatLabel);
            intervalRow.Children.Add(_formatBox);
            _fieldPanel.Children.Add(intervalRow);

            _urlBox = CreateTextBox(380);
            _urlBox.TextChanged += OnTextChanged;
            _fieldPanel.Children.Add(CreateFieldRow("链接", _urlBox));

            StackPanel enabledRow = new StackPanel();
            enabledRow.Orientation = Orientation.Horizontal;
            enabledRow.Margin = new Thickness(0, 8, 0, 0);
            enabledRow.Children.Add(CreateFieldLabel("状态", 60));

            _enabledBox = new CheckBox();
            _enabledBox.Content = "启用";
            _enabledBox.Template = SettingsWindow.CreateCheckBoxTemplate();
            SettingsWindow.ApplyCheckTheme(_enabledBox);
            _enabledBox.Checked += OnEnabledChanged;
            _enabledBox.Unchecked += OnEnabledChanged;
            _enabledBox.VerticalAlignment = VerticalAlignment.Center;
            enabledRow.Children.Add(_enabledBox);
            _fieldPanel.Children.Add(enabledRow);

            _root.Children.Add(_fieldPanel);

            RebuildList(-1);
        }

        internal FrameworkElement Root
        {
            get { return _root; }
        }

        internal IList<CustomCommandSettings> Commands
        {
            get { return _commands; }
        }

        /// <summary>
        /// 校验工作副本；通过则整体写回 source。失败时不改动 source。
        /// </summary>
        internal bool TryCommit(out string error)
        {
            if (!SettingsValidation.ValidateCommands(_commands, out error))
            {
                return false;
            }

            if (_source != null)
            {
                _source.Clear();
                for (int index = 0; index < _commands.Count; index++)
                {
                    _source.Add(_commands[index]);
                }
            }

            // 标签可能被截断，刷新一次列表文案（选中项保持不变）。
            RebuildList(_list.SelectedIndex);
            return true;
        }

        /// <summary>
        /// 由 SettingsWindow.ApplyTheme 调用。字段控件由设置窗口的 ThemeElement 统一上色，
        /// 这里只处理 ListBox 与 ListBoxItem（它们不在可视树的 Panel 子级里）。
        /// </summary>
        internal void ApplyTheme()
        {
            if (_list == null)
            {
                return;
            }

            _list.Background = new SolidColorBrush(UiTheme.InputBackground);
            _list.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            _list.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);

            for (int index = 0; index < _list.Items.Count; index++)
            {
                ListBoxItem item = _list.Items[index] as ListBoxItem;
                if (item == null)
                {
                    continue;
                }

                item.Background = new SolidColorBrush(UiTheme.InputBackground);
                item.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
                item.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
            }

            UpdateItemTextColors();
        }

        private static CustomCommandSettings Clone(CustomCommandSettings command)
        {
            CustomCommandSettings copy = new CustomCommandSettings();
            if (command == null)
            {
                return copy;
            }

            copy.Id = command.Id == null ? string.Empty : command.Id;
            copy.Label = command.Label == null ? string.Empty : command.Label;
            copy.Command = command.Command == null ? string.Empty : command.Command;
            copy.IntervalSeconds = command.IntervalSeconds;
            copy.Format = command.Format == null ? "text" : command.Format;
            copy.Url = command.Url == null ? string.Empty : command.Url;
            copy.Enabled = command.Enabled;
            return copy;
        }

        private static TextBox CreateTextBox(double width)
        {
            TextBox box = new TextBox();
            box.Height = 28;
            box.Width = width;
            box.Padding = new Thickness(6, 4, 6, 4);
            box.VerticalAlignment = VerticalAlignment.Center;
            return box;
        }

        /// <summary>
        /// 字段名。深色主题下不显式设 Foreground 会变成黑字，因此在这里当场设置。
        /// </summary>
        private static TextBlock CreateFieldLabel(string text, double width)
        {
            TextBlock label = new TextBlock();
            label.Text = text;
            label.Width = width;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            return label;
        }

        private static StackPanel CreateFieldRow(string labelText, UIElement control)
        {
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 6, 0, 0);
            row.Children.Add(CreateFieldLabel(labelText, 60));
            row.Children.Add(control);
            return row;
        }

        private static string DisplayText(CustomCommandSettings command)
        {
            if (command == null)
            {
                return "(未命名)";
            }

            string label = command.Label == null ? string.Empty : command.Label.Trim();
            if (label.Length > 0)
            {
                return label;
            }

            string id = command.Id == null ? string.Empty : command.Id.Trim();
            return id.Length > 0 ? id : "(未命名)";
        }

        private CustomCommandSettings SelectedCommand
        {
            get
            {
                ListBoxItem item = _list == null ? null : _list.SelectedItem as ListBoxItem;
                return item == null ? null : item.Tag as CustomCommandSettings;
            }
        }

        private void RebuildList(int selectIndex)
        {
            _loading = true;
            try
            {
                _list.Items.Clear();
                for (int index = 0; index < _commands.Count; index++)
                {
                    CustomCommandSettings command = _commands[index];

                    TextBlock text = new TextBlock();
                    text.Text = DisplayText(command);
                    text.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
                    text.TextTrimming = TextTrimming.CharacterEllipsis;

                    ListBoxItem item = new ListBoxItem();
                    item.Content = text;
                    item.Tag = command;
                    item.Template = _itemTemplate;
                    item.Background = new SolidColorBrush(UiTheme.InputBackground);
                    item.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
                    item.BorderBrush = new SolidColorBrush(UiTheme.InputBorder);
                    _list.Items.Add(item);
                }

                if (selectIndex >= 0 && selectIndex < _list.Items.Count)
                {
                    _list.SelectedIndex = selectIndex;
                }
                else if (_list.Items.Count > 0 && selectIndex >= _list.Items.Count)
                {
                    _list.SelectedIndex = _list.Items.Count - 1;
                }
            }
            finally
            {
                _loading = false;
            }

            UpdateItemTextColors();
            LoadFields(SelectedCommand);
        }

        private void LoadFields(CustomCommandSettings command)
        {
            _loading = true;
            try
            {
                if (command == null)
                {
                    _labelBox.Text = string.Empty;
                    _commandBox.Text = string.Empty;
                    _intervalBox.Text = string.Empty;
                    _urlBox.Text = string.Empty;
                    _formatBox.SelectedIndex = 0;
                    _enabledBox.IsChecked = true;
                }
                else
                {
                    _labelBox.Text = command.Label == null ? string.Empty : command.Label;
                    _commandBox.Text = command.Command == null ? string.Empty : command.Command;
                    _intervalBox.Text = command.IntervalSeconds.ToString(
                        CultureInfo.InvariantCulture);
                    _urlBox.Text = command.Url == null ? string.Empty : command.Url;
                    _formatBox.SelectedIndex = string.Equals(
                        command.Format,
                        "json",
                        StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : 0;
                    _enabledBox.IsChecked = command.Enabled;
                }
            }
            finally
            {
                _loading = false;
            }

            _fieldPanel.IsEnabled = command != null;
            _deleteButton.IsEnabled = command != null;
        }

        private void UpdateItemTextColors()
        {
            for (int index = 0; index < _list.Items.Count; index++)
            {
                ListBoxItem item = _list.Items[index] as ListBoxItem;
                if (item == null)
                {
                    continue;
                }

                TextBlock text = item.Content as TextBlock;
                if (text == null)
                {
                    continue;
                }

                text.Foreground = new SolidColorBrush(
                    item.IsSelected ? UiTheme.AccentText : UiTheme.AppTextPrimary);
            }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            LoadFields(SelectedCommand);
            UpdateItemTextColors();
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFieldValues();
        }

        private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFieldValues();
        }

        private void OnEnabledChanged(object sender, RoutedEventArgs e)
        {
            ApplyFieldValues();
        }

        private void ApplyFieldValues()
        {
            if (_loading)
            {
                return;
            }

            CustomCommandSettings command = SelectedCommand;
            if (command == null)
            {
                return;
            }

            command.Label = _labelBox.Text == null ? string.Empty : _labelBox.Text.Trim();
            command.Command = _commandBox.Text == null ? string.Empty : _commandBox.Text.Trim();
            command.Url = _urlBox.Text == null ? string.Empty : _urlBox.Text.Trim();
            command.Enabled = _enabledBox.IsChecked == true;
            command.Format = GetSelectedFormat();

            // 非数字时写入 -1，交给 SettingsValidation 报「刷新间隔需在 5-3600 秒之间」，
            // 而不是悄悄保留旧值。
            int interval;
            if (int.TryParse(
                    _intervalBox.Text == null ? string.Empty : _intervalBox.Text.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out interval))
            {
                command.IntervalSeconds = interval;
            }
            else
            {
                command.IntervalSeconds = -1;
            }

            ListBoxItem item = _list.SelectedItem as ListBoxItem;
            if (item != null)
            {
                TextBlock text = item.Content as TextBlock;
                if (text != null)
                {
                    text.Text = DisplayText(command);
                }
            }
        }

        private string GetSelectedFormat()
        {
            ComboBoxItem item = _formatBox == null ? null : _formatBox.SelectedItem as ComboBoxItem;
            string format = item == null ? null : item.Tag as string;
            return string.Equals(format, "json", StringComparison.Ordinal) ? "json" : "text";
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            CustomCommandSettings command = new CustomCommandSettings();
            command.Id = CreateId();
            command.Label = string.Empty;
            command.Command = string.Empty;
            command.IntervalSeconds = 30;
            command.Format = "text";
            command.Url = string.Empty;
            command.Enabled = true;

            _commands.Add(command);
            RebuildList(_commands.Count - 1);
            _commandBox.Focus();
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= _commands.Count)
            {
                return;
            }

            _commands.RemoveAt(index);

            // 选中相邻项：删中间选原位置，删末尾选新的最后一条。
            int next = index >= _commands.Count ? _commands.Count - 1 : index;
            RebuildList(next);
        }

        private string CreateId()
        {
            int index = _commands.Count + 1;
            while (true)
            {
                string candidate = "cmd" + index.ToString(CultureInfo.InvariantCulture);
                bool exists = false;
                for (int commandIndex = 0; commandIndex < _commands.Count; commandIndex++)
                {
                    CustomCommandSettings command = _commands[commandIndex];
                    if (command != null
                        && string.Equals(command.Id, candidate, StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    return candidate;
                }

                index++;
            }
        }

        private static ControlTemplate CreateListBoxItemTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ListBoxItem));

            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ItemBorder";
            border.SetValue(
                Border.BackgroundProperty,
                new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(
                Border.BorderBrushProperty,
                new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
            border.SetValue(Border.PaddingProperty, new Thickness(8, 5, 8, 5));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);

            FrameworkElementFactory presenter = new FrameworkElementFactory(
                typeof(ContentPresenter));
            presenter.SetValue(
                FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Left);
            presenter.SetValue(
                FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            border.AppendChild(presenter);

            template.VisualTree = border;

            // 悬停在前、选中在后，后加的 Trigger 优先级更高，选中色不会被悬停盖掉。
            Trigger hoverTrigger = new Trigger();
            hoverTrigger.Property = UIElement.IsMouseOverProperty;
            hoverTrigger.Value = true;
            hoverTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(UiTheme.ButtonHover),
                "ItemBorder"));
            template.Triggers.Add(hoverTrigger);

            Trigger selectedTrigger = new Trigger();
            selectedTrigger.Property = ListBoxItem.IsSelectedProperty;
            selectedTrigger.Value = true;
            selectedTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(UiTheme.Accent),
                "ItemBorder"));
            template.Triggers.Add(selectedTrigger);

            return template;
        }
    }
}
