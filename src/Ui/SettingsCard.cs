using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VibeBar
{
    /// <summary>
    /// 设置窗口的卡片构造 helper（代码构建，无 XAML）。
    /// 卡片 = 圆角边框 + 左侧图标 + 标题/说明 + 右侧控件；颜色全部来自 UiTheme，
    /// ApplyTheme 时由 SettingsWindow 统一回刷（卡片内控件交给 ThemeElement 递归）。
    /// </summary>
    internal static class SettingsCard
    {
        internal const double CardPadding = 16.0;
        internal const double CardCornerRadius = 8.0;
        internal const double CardSpacing = 12.0;
        internal const double IconFontSize = 18.0;
        internal const double IconWidth = 18.0;
        internal const double TitleFontSize = 15.0;
        internal const double DescriptionFontSize = 12.0;
        internal const double RowLabelWidth = 140.0;
        internal const double RowsWidth = 320.0;
        internal const double RowsDescriptionWidth = 240.0;

        internal static readonly FontFamily IconFont = new FontFamily("Segoe MDL2 Assets");

        /// <summary>卡片内需要单独上色的元素（边框由 ApplyTheme 处理）。</summary>
        internal sealed class CardParts
        {
            internal StackPanel TextStack;
            internal TextBlock Icon;
            internal TextBlock Title;
            internal TextBlock Description;
        }

        /// <summary>单控件：标题/说明在左，控件右对齐垂直居中。</summary>
        internal static Border Create(
            string icon,
            string title,
            string description,
            FrameworkElement control)
        {
            Grid grid = CreateGrid();
            CardParts parts = AddHeader(grid, icon, title, description);

            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                control.HorizontalAlignment = HorizontalAlignment.Right;
                control.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(control, 2);
                grid.Children.Add(control);
            }

            return Wrap(grid, parts);
        }

        /// <summary>
        /// 多行：每行 = 左标签（140 宽，次要色）+ 右控件。
        /// rows 由 CreateRow 构造；也可直接塞入提示文本等任意元素。
        /// </summary>
        internal static Border CreateRows(
            string icon,
            string title,
            string description,
            IList<FrameworkElement> rows)
        {
            Grid grid = CreateGrid();
            CardParts parts = AddHeader(grid, icon, title, description);
            if (parts.TextStack != null)
            {
                // 多行卡片的标题贴顶，与第一行标签对齐。
                parts.TextStack.VerticalAlignment = VerticalAlignment.Top;
            }

            // 说明用固定宽度列，不再吃「剩余空间」：否则控件一宽，说明就被挤成每行四五个字。
            if (parts.Description != null)
            {
                parts.Description.Width = RowsDescriptionWidth;
                parts.Description.MaxWidth = RowsDescriptionWidth;
                parts.Description.HorizontalAlignment = HorizontalAlignment.Left;
            }

            // 控件列右对齐，最小 220，避免行区被标题列反向挤压。
            grid.ColumnDefinitions[2].MinWidth = 220.0;

            StackPanel stack = new StackPanel();
            stack.Orientation = Orientation.Vertical;
            stack.VerticalAlignment = VerticalAlignment.Top;
            stack.HorizontalAlignment = HorizontalAlignment.Right;
            // 固定行区宽度：各卡片的行标签落在同一条竖线上，不会因为控件宽度不同而漂移。
            stack.Width = RowsWidth;
            stack.Margin = new Thickness(16, 0, 0, 0);

            if (rows != null)
            {
                for (int index = 0; index < rows.Count; index++)
                {
                    FrameworkElement row = rows[index];
                    if (row == null)
                    {
                        continue;
                    }

                    row.Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 8, 0, 0);
                    stack.Children.Add(row);
                }
            }

            Grid.SetColumn(stack, 2);
            grid.Children.Add(stack);
            return Wrap(grid, parts);
        }

        /// <summary>自定义内容：标题/说明在上，内容在下（通栏）。</summary>
        internal static Border CreateCustom(
            string icon,
            string title,
            string description,
            FrameworkElement content)
        {
            Grid grid = new Grid();
            ColumnDefinition iconColumn = new ColumnDefinition();
            iconColumn.Width = GridLength.Auto;
            ColumnDefinition textColumn = new ColumnDefinition();
            textColumn.Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions.Add(iconColumn);
            grid.ColumnDefinitions.Add(textColumn);

            RowDefinition headerRow = new RowDefinition();
            headerRow.Height = GridLength.Auto;
            RowDefinition bodyRow = new RowDefinition();
            bodyRow.Height = GridLength.Auto;
            grid.RowDefinitions.Add(headerRow);
            grid.RowDefinitions.Add(bodyRow);

            TextBlock iconText = CreateIcon(icon);
            Grid.SetRow(iconText, 0);
            Grid.SetColumn(iconText, 0);
            grid.Children.Add(iconText);

            StackPanel text = CreateTextStack(title, description);
            Grid.SetRow(text, 0);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (content != null)
            {
                content.Margin = new Thickness(0, 12, 0, 0);
                Grid.SetRow(content, 1);
                Grid.SetColumnSpan(content, 2);
                grid.Children.Add(content);
            }

            CardParts parts = new CardParts();
            parts.TextStack = text;
            parts.Icon = iconText;
            parts.Title = text.Children[0] as TextBlock;
            parts.Description = text.Children.Count > 1 ? text.Children[1] as TextBlock : null;
            return Wrap(grid, parts);
        }

        /// <summary>一行：左标签（140 宽，次要色）+ 右控件。</summary>
        internal static Grid CreateRow(string labelText, FrameworkElement control)
        {
            TextBlock label = new TextBlock();
            label.Text = labelText;
            label.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            return CreateRowCore(label, control);
        }

        /// <summary>同上，但复用已有的标签元素（例如文案会动态变化的偏移标签）。</summary>
        internal static Grid CreateRow(TextBlock label, FrameworkElement control)
        {
            if (label != null)
            {
                label.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            }

            return CreateRowCore(label, control);
        }

        /// <summary>按当前主题回刷卡片外壳与文字色。</summary>
        internal static void ApplyTheme(Border card)
        {
            if (card == null)
            {
                return;
            }

            card.Background = new SolidColorBrush(UiTheme.CardBackground);
            card.BorderBrush = new SolidColorBrush(UiTheme.CardBorder);

            CardParts parts = card.Tag as CardParts;
            if (parts == null)
            {
                return;
            }

            if (parts.Icon != null)
            {
                parts.Icon.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            }

            if (parts.Title != null)
            {
                parts.Title.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            }

            if (parts.Description != null)
            {
                parts.Description.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            }
        }

        private static Grid CreateGrid()
        {
            Grid grid = new Grid();

            ColumnDefinition iconColumn = new ColumnDefinition();
            iconColumn.Width = GridLength.Auto;
            ColumnDefinition textColumn = new ColumnDefinition();
            textColumn.Width = new GridLength(1, GridUnitType.Star);
            ColumnDefinition controlColumn = new ColumnDefinition();
            controlColumn.Width = GridLength.Auto;

            grid.ColumnDefinitions.Add(iconColumn);
            grid.ColumnDefinitions.Add(textColumn);
            grid.ColumnDefinitions.Add(controlColumn);
            return grid;
        }

        private static CardParts AddHeader(
            Grid grid,
            string icon,
            string title,
            string description)
        {
            TextBlock iconText = CreateIcon(icon);
            Grid.SetColumn(iconText, 0);
            grid.Children.Add(iconText);

            StackPanel text = CreateTextStack(title, description);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            CardParts parts = new CardParts();
            parts.TextStack = text;
            parts.Icon = iconText;
            parts.Title = text.Children[0] as TextBlock;
            parts.Description = text.Children.Count > 1 ? text.Children[1] as TextBlock : null;
            return parts;
        }

        private static TextBlock CreateIcon(string icon)
        {
            TextBlock iconText = new TextBlock();
            iconText.Text = icon == null ? string.Empty : icon;
            iconText.FontFamily = IconFont;
            iconText.FontSize = IconFontSize;
            iconText.Width = IconWidth;
            iconText.Margin = new Thickness(0, 1, 12, 0);
            iconText.VerticalAlignment = VerticalAlignment.Top;
            iconText.TextAlignment = TextAlignment.Left;
            iconText.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
            return iconText;
        }

        private static StackPanel CreateTextStack(string title, string description)
        {
            StackPanel text = new StackPanel();
            text.Orientation = Orientation.Vertical;
            text.VerticalAlignment = VerticalAlignment.Center;

            TextBlock titleText = new TextBlock();
            titleText.Text = title == null ? string.Empty : title;
            titleText.FontSize = TitleFontSize;
            titleText.FontWeight = FontWeights.SemiBold;
            titleText.TextWrapping = TextWrapping.Wrap;
            titleText.Foreground = new SolidColorBrush(UiTheme.AppTextPrimary);
            text.Children.Add(titleText);

            if (!string.IsNullOrEmpty(description))
            {
                TextBlock descriptionText = new TextBlock();
                descriptionText.Text = description;
                descriptionText.FontSize = DescriptionFontSize;
                descriptionText.TextWrapping = TextWrapping.Wrap;
                descriptionText.Margin = new Thickness(0, 3, 0, 0);
                descriptionText.MaxWidth = 360.0;
                // Stretch + MaxWidth 会被 WPF 居中，显式左对齐，避免说明文字比标题缩进一截。
                descriptionText.HorizontalAlignment = HorizontalAlignment.Left;
                descriptionText.TextAlignment = TextAlignment.Left;
                descriptionText.Foreground = new SolidColorBrush(UiTheme.AppTextSecondary);
                text.Children.Add(descriptionText);
            }

            return text;
        }

        private static Grid CreateRowCore(TextBlock label, FrameworkElement control)
        {
            Grid row = new Grid();

            ColumnDefinition labelColumn = new ColumnDefinition();
            labelColumn.Width = GridLength.Auto;
            ColumnDefinition controlColumn = new ColumnDefinition();
            controlColumn.Width = new GridLength(1, GridUnitType.Star);
            row.ColumnDefinitions.Add(labelColumn);
            row.ColumnDefinitions.Add(controlColumn);

            if (label != null)
            {
                label.Width = RowLabelWidth;
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(label, 0);
                row.Children.Add(label);
            }

            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                control.HorizontalAlignment = HorizontalAlignment.Left;
                Grid.SetColumn(control, 1);
                row.Children.Add(control);
            }

            return row;
        }

        private static Border Wrap(Grid content, CardParts parts)
        {
            Border card = new Border();
            card.CornerRadius = new CornerRadius(CardCornerRadius);
            card.BorderThickness = new Thickness(1);
            card.Padding = new Thickness(CardPadding);
            card.Margin = new Thickness(0, 0, 0, CardSpacing);
            card.SnapsToDevicePixels = true;
            card.Background = new SolidColorBrush(UiTheme.CardBackground);
            card.BorderBrush = new SolidColorBrush(UiTheme.CardBorder);
            card.Tag = parts;
            card.Child = content;
            return card;
        }
    }
}
