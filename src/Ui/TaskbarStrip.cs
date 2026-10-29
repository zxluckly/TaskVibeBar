using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VibeBar
{
    /// <summary>
    /// 任务栏内的透明胶囊条：只消费 SegmentVisual，不接触 Provider。
    /// 宽度不足时按「Git → 非等待确认的会话 → 额度」折叠，额度为最低保留项。
    /// </summary>
    internal sealed class TaskbarStrip : Border
    {
        internal delegate void HoverChangedHandler(bool hovered);

        internal delegate void SegmentClickedHandler(SegmentKind kind);

        internal event HoverChangedHandler HoverChanged;
        internal event SegmentClickedHandler SegmentClicked;

        private readonly StackPanel _segments;
        private readonly SolidColorBrush _backgroundBrush;
        private readonly List<TextBlock> _labels = new List<TextBlock>();
        private readonly List<ColorRole> _labelRoles = new List<ColorRole>();
        private readonly List<Border> _separators = new List<Border>();
        private readonly List<SolidColorBrush> _separatorBrushes =
            new List<SolidColorBrush>();
        private readonly List<SolidColorBrush> _hoverBrushes =
            new List<SolidColorBrush>();

        private SegmentVisual[] _visuals;
        private bool _hovered;
        private Color? _customHoverColor;
        private double _hoverTargetOpacity = 235.0 / 255.0;
        private bool _hasAvailableWidth;
        private double _availableWidth = double.PositiveInfinity;
        private double _minWidth = 120.0;
        private int _foldCount;
        private bool _contentHidden;
        private int _hoveredSegmentIndex = -1;

        internal TaskbarStrip(SegmentVisual[] visuals)
        {
            CornerRadius = new CornerRadius(UiTheme.CornerRadiusValue);
            Padding = new Thickness(
                UiTheme.BarHorizontalPadding,
                UiTheme.BarVerticalPadding,
                UiTheme.BarHorizontalPadding,
                UiTheme.BarVerticalPadding);
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            _backgroundBrush = new SolidColorBrush(UiTheme.BarBackground);
            // 不能使用 alpha=0：WPF 分层窗口中完全透明区域不参与鼠标命中。
            _backgroundBrush.Opacity = 1.0 / 255.0;
            Background = _backgroundBrush;

            _segments = new StackPanel();
            _segments.Orientation = Orientation.Horizontal;
            _segments.Height = UiTheme.BarContentHeight;
            _segments.VerticalAlignment = VerticalAlignment.Center;
            Child = _segments;

            SetVisuals(visuals);

            MouseEnter += delegate { SetHovered(true); };
            MouseLeave += delegate { SetHovered(false); };
        }

        internal SegmentVisual[] GetVisuals()
        {
            return _visuals;
        }

        /// <summary>
        /// 自定义悬停背景。colorText 为空或非法时跟随系统主题色；
        /// opacity 为 0-255，0 表示悬停时也完全透明。
        /// </summary>
        internal void SetHoverStyle(string colorText, int opacity)
        {
            Color parsed;
            if (TryParseColor(colorText, out parsed))
            {
                _customHoverColor = parsed;
            }
            else
            {
                _customHoverColor = null;
            }

            if (opacity < 0)
            {
                opacity = 0;
            }
            else if (opacity > 255)
            {
                opacity = 255;
            }

            _hoverTargetOpacity = opacity / 255.0;
            ApplyTheme();

            if (_hovered)
            {
                _backgroundBrush.BeginAnimation(
                    Brush.OpacityProperty,
                    new DoubleAnimation(
                        _hoverTargetOpacity,
                        new Duration(TimeSpan.FromMilliseconds(1))));
            }
        }

        private static bool TryParseColor(string text, out Color color)
        {
            color = Colors.Transparent;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string value = text.Trim();
            if (!value.StartsWith("#", StringComparison.Ordinal))
            {
                value = "#" + value;
            }

            if (value.Length != 7)
            {
                return false;
            }

            int red;
            int green;
            int blue;
            if (!int.TryParse(
                    value.Substring(1, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out red)
                || !int.TryParse(
                    value.Substring(3, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out green)
                || !int.TryParse(
                    value.Substring(5, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out blue))
            {
                return false;
            }

            color = Color.FromRgb((byte)red, (byte)green, (byte)blue);
            return true;
        }

        internal bool ContentHidden
        {
            get { return _contentHidden; }
        }

        internal int FoldCount
        {
            get { return _foldCount; }
        }

        internal void SetVisuals(SegmentVisual[] visuals)
        {
            _visuals = visuals == null ? new SegmentVisual[0] : visuals;
            Rebuild();
        }

        /// <summary>
        /// 由 TaskbarHost 在布局前传入可用宽度（DIP）。minWidth 以下只保留额度段。
        /// </summary>
        internal void SetAvailableWidth(double availableWidth, double minWidth)
        {
            if (double.IsNaN(availableWidth) || availableWidth < 0)
            {
                availableWidth = 0;
            }

            if (double.IsNaN(minWidth) || minWidth < 0)
            {
                minWidth = 0;
            }

            bool changed = !_hasAvailableWidth
                || Math.Abs(_availableWidth - availableWidth) >= 0.5
                || Math.Abs(_minWidth - minWidth) >= 0.5;

            _hasAvailableWidth = true;
            _availableWidth = availableWidth;
            _minWidth = minWidth;

            if (changed)
            {
                Rebuild();
            }
        }

        internal void ApplyTheme()
        {
            Color baseColor = UiTheme.BarBackground;
            if (_customHoverColor.HasValue)
            {
                baseColor = _customHoverColor.Value;
            }

            // 颜色只保留 RGB，透明度由 _backgroundBrush.Opacity / 悬停目标值控制，
            // 避免主题色自带的 alpha 与用户设置的透明度叠加。
            _backgroundBrush.Color = Color.FromRgb(
                baseColor.R,
                baseColor.G,
                baseColor.B);

            for (int index = 0; index < _labels.Count; index++)
            {
                ColorRole role = index < _labelRoles.Count
                    ? _labelRoles[index]
                    : ColorRole.Neutral;
                _labels[index].Foreground =
                    new SolidColorBrush(UiTheme.RoleColor(role));
            }

            for (int index = 0; index < _separatorBrushes.Count; index++)
            {
                _separatorBrushes[index].Color = UiTheme.Separator;
            }

            for (int index = 0; index < _hoverBrushes.Count; index++)
            {
                _hoverBrushes[index].Color = UiTheme.SegmentHover;
            }
        }

        /// <summary>
        /// 纯计算：返回需要折叠的段数；-1 表示只保留额度仍放不下，应隐藏整条。
        /// 折叠顺序固定为 Git、非等待确认的会话；额度不折叠（若仅额度也放不下则隐藏）。
        /// </summary>
        internal static int CalculateFoldCount(
            SegmentVisual[] visuals,
            double availableWidth,
            double minWidth,
            double pixelsPerDip)
        {
            if (visuals == null || visuals.Length == 0)
            {
                return 0;
            }

            if (double.IsInfinity(availableWidth))
            {
                return 0;
            }

            double limit = availableWidth - UiTheme.BarHorizontalPadding * 2.0;
            if (limit <= 0)
            {
                return -1;
            }

            bool[] folded = new bool[visuals.Length];
            if (Fits(visuals, folded, 0, false, limit, pixelsPerDip))
            {
                return 0;
            }

            if (availableWidth < minWidth)
            {
                bool[] quotaOnly = new bool[visuals.Length];
                int nonQuotaCount = 0;
                for (int index = 0; index < visuals.Length; index++)
                {
                    SegmentVisual visual = visuals[index];
                    if (visual == null || visual.Kind == SegmentKind.Quota)
                    {
                        continue;
                    }

                    quotaOnly[index] = true;
                    nonQuotaCount++;
                }

                if (Fits(visuals, quotaOnly, nonQuotaCount, false, limit, pixelsPerDip))
                {
                    return nonQuotaCount;
                }

                return -1;
            }

            List<int> order = BuildFoldOrder(visuals);
            int count = 0;
            for (int index = 0; index < order.Count; index++)
            {
                folded[order[index]] = true;
                count++;
                if (Fits(visuals, folded, count, true, limit, pixelsPerDip))
                {
                    return count;
                }
            }

            // 极窄时 +N 本身可能放不下；只要剩余段（通常只剩额度）能放下就不隐藏。
            if (count > 0
                && Fits(visuals, folded, count, false, limit, pixelsPerDip))
            {
                return count;
            }

            return -1;
        }

        internal static double MeasureSegmentWidth(
            SegmentVisual visual,
            double pixelsPerDip)
        {
            if (visual == null)
            {
                return 0;
            }

            // 预留 2px：实际 TextBlock 使用 tabular 数字，FormattedText 估算可能有亚像素差异。
            return MeasureTextWidth(FormatLabel(visual), pixelsPerDip)
                + UiTheme.SegmentHorizontalPadding * 2.0
                + 2.0;
        }

        internal static string FormatLabel(SegmentVisual visual)
        {
            if (visual == null)
            {
                return string.Empty;
            }

            return string.IsNullOrEmpty(visual.Glyph)
                ? visual.Text
                : visual.Glyph + " " + visual.Text;
        }

        private static List<int> BuildFoldOrder(SegmentVisual[] visuals)
        {
            List<int> order = new List<int>();
            for (int index = 0; index < visuals.Length; index++)
            {
                if (visuals[index] != null && visuals[index].Kind == SegmentKind.Git)
                {
                    order.Add(index);
                }
            }

            for (int index = 0; index < visuals.Length; index++)
            {
                SegmentVisual visual = visuals[index];
                if (visual != null
                    && visual.Kind == SegmentKind.Agent
                    && !visual.Pulse)
                {
                    order.Add(index);
                }
            }

            // 可选段（PR / 端口 / 自定义命令）优先折叠，额度与等待确认的会话最后折叠。
            SegmentKind[] optional = new SegmentKind[]
            {
                SegmentKind.Pr,
                SegmentKind.Port,
                SegmentKind.Custom
            };

            for (int kindIndex = 0; kindIndex < optional.Length; kindIndex++)
            {
                for (int index = 0; index < visuals.Length; index++)
                {
                    SegmentVisual visual = visuals[index];
                    if (visual != null && visual.Kind == optional[kindIndex])
                    {
                        order.Add(index);
                    }
                }
            }

            return order;
        }

        private static bool[] BuildFoldPlan(
            SegmentVisual[] visuals,
            int foldCount)
        {
            bool[] folded = new bool[visuals.Length];
            if (foldCount <= 0)
            {
                return folded;
            }

            List<int> order = BuildFoldOrder(visuals);
            int count = Math.Min(foldCount, order.Count);
            for (int index = 0; index < count; index++)
            {
                folded[order[index]] = true;
            }

            return folded;
        }

        private static bool Fits(
            SegmentVisual[] visuals,
            bool[] folded,
            int foldedCount,
            bool includePlus,
            double limit,
            double pixelsPerDip)
        {
            double total = 0;
            int visible = 0;
            for (int index = 0; index < visuals.Length; index++)
            {
                if (visuals[index] == null || (folded != null && folded[index]))
                {
                    continue;
                }

                if (visible > 0)
                {
                    total += UiTheme.SeparatorWidth
                        + UiTheme.SeparatorSpacing * 2.0;
                }

                total += MeasureSegmentWidth(visuals[index], pixelsPerDip);
                visible++;
            }

            if (includePlus && foldedCount > 0)
            {
                if (visible > 0)
                {
                    total += UiTheme.SeparatorWidth
                        + UiTheme.SeparatorSpacing * 2.0;
                }

                total += MeasurePlusWidth(foldedCount, pixelsPerDip);
            }

            return total <= limit + 0.5;
        }

        private static double MeasurePlusWidth(
            int foldedCount,
            double pixelsPerDip)
        {
            string text = "+" + foldedCount.ToString(CultureInfo.InvariantCulture);
            return MeasureTextWidth(text, pixelsPerDip)
                + UiTheme.SegmentHorizontalPadding * 2.0
                + 2.0;
        }

        private static double MeasureTextWidth(
            string text,
            double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            if (double.IsNaN(pixelsPerDip) || pixelsPerDip <= 0)
            {
                pixelsPerDip = 1.0;
            }

            Typeface typeface = new Typeface(
                UiTheme.UiFont,
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal);

            FormattedText formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                UiTheme.SegmentFontSize,
                Brushes.Black,
                pixelsPerDip);

            return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace + 0.5);
        }

        private double GetPixelsPerDip()
        {
            try
            {
                return VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch
            {
                return 1.0;
            }
        }

        private void Rebuild()
        {
            _segments.Children.Clear();
            _labels.Clear();
            _labelRoles.Clear();
            _separators.Clear();
            _separatorBrushes.Clear();
            _hoverBrushes.Clear();
            _hoveredSegmentIndex = -1;
            _foldCount = 0;
            _contentHidden = false;

            SegmentVisual[] visuals = _visuals;
            if (visuals == null || visuals.Length == 0)
            {
                Visibility = Visibility.Visible;
                return;
            }

            double pixelsPerDip = GetPixelsPerDip();
            int foldCount = CalculateFoldCount(
                visuals,
                _availableWidth,
                _minWidth,
                pixelsPerDip);

            if (foldCount < 0)
            {
                _contentHidden = true;
                Visibility = Visibility.Collapsed;
                return;
            }

            Visibility = Visibility.Visible;
            _foldCount = foldCount;

            bool[] folded = BuildFoldPlan(visuals, foldCount);
            double limit = double.IsInfinity(_availableWidth)
                ? double.PositiveInfinity
                : _availableWidth - UiTheme.BarHorizontalPadding * 2.0;

            bool showPlus = foldCount > 0
                && Fits(visuals, folded, foldCount, true, limit, pixelsPerDip);
            if (foldCount > 0
                && !showPlus
                && !Fits(visuals, folded, foldCount, false, limit, pixelsPerDip))
            {
                _contentHidden = true;
                Visibility = Visibility.Collapsed;
                return;
            }

            bool first = true;
            for (int index = 0; index < visuals.Length; index++)
            {
                SegmentVisual visual = visuals[index];
                if (visual == null || folded[index])
                {
                    continue;
                }

                if (!first)
                {
                    AddSeparator();
                }

                AddSegment(visual, true);
                first = false;
            }

            if (showPlus)
            {
                if (!first)
                {
                    AddSeparator();
                }

                AddPlusSegment(foldCount);
            }

            if (_segments.Children.Count == 0)
            {
                _contentHidden = true;
                Visibility = Visibility.Collapsed;
            }
        }

        private void AddSeparator()
        {
            SolidColorBrush brush = new SolidColorBrush(UiTheme.Separator);
            Border separator = new Border();
            separator.Width = UiTheme.SeparatorWidth;
            separator.Height = UiTheme.SeparatorHeight;
            separator.Margin = new Thickness(
                UiTheme.SeparatorSpacing,
                0,
                UiTheme.SeparatorSpacing,
                0);
            separator.VerticalAlignment = VerticalAlignment.Center;
            separator.Background = brush;
            separator.IsHitTestVisible = false;

            _segments.Children.Add(separator);
            _separators.Add(separator);
            _separatorBrushes.Add(brush);
        }

        private void AddSegment(SegmentVisual visual, bool clickable)
        {
            int renderIndex = _hoverBrushes.Count;

            SolidColorBrush hoverBrush = new SolidColorBrush(UiTheme.SegmentHover);
            hoverBrush.Opacity = 0.0;

            Border segment = new Border();
            segment.Height = UiTheme.BarContentHeight;
            segment.CornerRadius = new CornerRadius(UiTheme.SegmentCornerRadius);
            segment.Padding = new Thickness(
                UiTheme.SegmentHorizontalPadding,
                0,
                UiTheme.SegmentHorizontalPadding,
                0);
            segment.VerticalAlignment = VerticalAlignment.Center;
            segment.Background = hoverBrush;

            // T1 复核结论：详情面板已是唯一的详情入口，段上再加 ToolTip 会创建
            // 额外的顶层窗口并干扰 MouseEnter/Leave，导致面板闪烁/不显示。
            // SegmentVisual.Tooltip 字段保留，不在任务栏段上使用。

            segment.MouseEnter += delegate
            {
                SetSegmentHovered(renderIndex, true);
            };
            segment.MouseLeave += delegate
            {
                SetSegmentHovered(renderIndex, false);
            };

            if (clickable)
            {
                SegmentKind kind = visual.Kind;
                segment.Cursor = Cursors.Hand;
                segment.MouseLeftButtonUp += delegate
                {
                    SegmentClickedHandler handler = SegmentClicked;
                    if (handler != null)
                    {
                        handler(kind);
                    }
                };
            }

            TextBlock label = new TextBlock();
            label.Text = FormatLabel(visual);
            label.FontFamily = UiTheme.UiFont;
            label.FontSize = UiTheme.SegmentFontSize;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);

            // 颜色必须在创建时按当前主题设置：Rebuild 会重建所有 TextBlock，
            // 若只依赖 ApplyTheme 上色，主题切换后的下一次重建（Provider 结果
            // 强制刷新）会把文字打回默认黑色，深色模式下就变成黑字黑底。
            label.Foreground = new SolidColorBrush(UiTheme.RoleColor(visual.Role));

            if (visual.Pulse)
            {
                StartPulse(label);
            }

            segment.Child = label;

            _hoverBrushes.Add(hoverBrush);
            _segments.Children.Add(segment);
            _labels.Add(label);
            _labelRoles.Add(visual.Role);
        }

        private void AddPlusSegment(int foldedCount)
        {
            SegmentVisual visual = new SegmentVisual();
            visual.Kind = SegmentKind.Agent;
            visual.Glyph = string.Empty;
            visual.Text = "+" + foldedCount.ToString(CultureInfo.InvariantCulture);
            visual.Tooltip = "已折叠 "
                + foldedCount.ToString(CultureInfo.InvariantCulture)
                + " 个段";
            visual.Role = ColorRole.Neutral;
            visual.Pulse = false;

            AddSegment(visual, false);
        }

        private static void StartPulse(TextBlock label)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                label.Opacity = 1.0;
                return;
            }

            DoubleAnimation animation = new DoubleAnimation(
                0.55,
                1.0,
                new Duration(TimeSpan.FromSeconds(1)));
            animation.AutoReverse = true;
            animation.RepeatBehavior = RepeatBehavior.Forever;
            animation.EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            };

            label.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        private void SetSegmentHovered(int index, bool hovered)
        {
            if (index < 0 || index >= _hoverBrushes.Count)
            {
                return;
            }

            if (hovered)
            {
                _hoveredSegmentIndex = index;
            }
            else if (_hoveredSegmentIndex == index)
            {
                _hoveredSegmentIndex = -1;
            }

            DoubleAnimation animation = new DoubleAnimation(
                hovered ? 1.0 : 0.0,
                new Duration(TimeSpan.FromMilliseconds(hovered ? 120 : 150)));
            animation.EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            };

            _hoverBrushes[index].BeginAnimation(
                Brush.OpacityProperty,
                animation);
        }

        private void SetHovered(bool hovered)
        {
            if (_hovered == hovered)
            {
                return;
            }

            _hovered = hovered;
            DoubleAnimation animation = new DoubleAnimation(
                hovered ? _hoverTargetOpacity : 1.0 / 255.0,
                new Duration(TimeSpan.FromMilliseconds(170)));
            animation.EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            };
            _backgroundBrush.BeginAnimation(Brush.OpacityProperty, animation);

            HoverChangedHandler handler = HoverChanged;
            if (handler != null)
            {
                handler(hovered);
            }
        }
    }
}
