using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        private readonly TextBlock _hookStatusText;

        internal event EventHandler Saved;

        internal SettingsWindow(AppSettings settings, SettingsStore settingsStore)
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
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(UiTheme.PanelBackground);
            Foreground = new SolidColorBrush(UiTheme.TextPrimary);
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

            _hookStatusText = new TextBlock();
            _hookStatusText.TextWrapping = TextWrapping.Wrap;
            _hookStatusText.Margin = new Thickness(0, 4, 0, 0);
            _hookStatusText.Opacity = 0.85;
            RefreshHookStatus();

            Content = BuildLayout();
        }

        private UIElement BuildLayout()
        {
            StackPanel root = new StackPanel();
            root.Margin = new Thickness(18);

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
                "安装到 %USERPROFILE%\\.claude\\settings.json，安装前会自动备份。"
                + "Hooks 对已启动的会话不生效，请启动新的 Claude Code 会话。");

            StackPanel hookButtons = new StackPanel();
            hookButtons.Orientation = Orientation.Horizontal;
            hookButtons.Margin = new Thickness(0, 8, 0, 0);

            Button installHooksButton = new Button();
            installHooksButton.Content = "安装 Claude Code Hooks";
            installHooksButton.Width = 172;
            installHooksButton.Height = 30;
            installHooksButton.Click += OnInstallHooks;
            hookButtons.Children.Add(installHooksButton);

            Button uninstallHooksButton = new Button();
            uninstallHooksButton.Content = "卸载 Hooks";
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
            try
            {
                installed = _hookInstaller.IsInstalled();
            }
            catch (Exception exception)
            {
                Log.Write("检查 Hooks 安装状态失败：" + exception.GetType().Name);
            }

            if (_settings.Hooks.Installed != installed)
            {
                _settings.Hooks.Installed = installed;
            }

            _hookStatusText.Text = installed
                ? "状态：已安装（对新会话生效）"
                : "状态：未安装";
        }

        private void OnInstallHooks(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                this,
                "安装前会把 settings.json 备份到同目录。"
                + "安装后请启动新的 Claude Code 会话，Hooks 才会生效。\n\n是否继续？",
                "安装 Claude Code Hooks",
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
            SaveHookState();
            RefreshHookStatus();

            MessageBox.Show(
                this,
                "Hooks 已安装。请启动新的 Claude Code 会话后生效。",
                "安装 Claude Code Hooks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OnUninstallHooks(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                this,
                "将只移除 VibeBar 的 Hooks 条目，其他配置与插件 hooks 不受影响。\n\n是否继续？",
                "卸载 Hooks",
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
            SaveHookState();
            RefreshHookStatus();

            MessageBox.Show(
                this,
                "Hooks 已卸载。",
                "卸载 Hooks",
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
