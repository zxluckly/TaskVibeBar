using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VibeBar
{
    /// <summary>
    /// 任务栏内的透明胶囊条：只消费 SegmentVisual，不接触 Provider。
    /// </summary>
    internal sealed class TaskbarStrip : Border
    {
        internal delegate void HoverChangedHandler(bool hovered);

        internal event HoverChangedHandler HoverChanged;

        private readonly StackPanel _segments;
        private readonly SolidColorBrush _backgroundBrush;
        private readonly List<TextBlock> _labels = new List<TextBlock>();

        private SegmentVisual[] _visuals;
        private bool _hovered;

        internal TaskbarStrip(SegmentVisual[] visuals)
        {
            CornerRadius = new CornerRadius(UiTheme.CornerRadiusValue);
            Padding = new Thickness(
                10,
                UiTheme.BarVerticalPadding,
                10,
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

        internal void SetVisuals(SegmentVisual[] visuals)
        {
            if (visuals == null)
            {
                visuals = new SegmentVisual[0];
            }

            _visuals = visuals;
            _segments.Children.Clear();
            _labels.Clear();

            for (int index = 0; index < visuals.Length; index++)
            {
                SegmentVisual visual = visuals[index];
                if (visual == null)
                {
                    continue;
                }

                Border segment = new Border();
                segment.Height = UiTheme.BarContentHeight;
                segment.Padding = new Thickness(0);
                segment.Margin = new Thickness(
                    index == 0 ? 0 : UiTheme.SegmentSpacing,
                    0,
                    0,
                    0);
                segment.Background = Brushes.Transparent;

                TextBlock label = new TextBlock();
                label.Text = string.IsNullOrEmpty(visual.Glyph)
                    ? visual.Text
                    : visual.Glyph + " " + visual.Text;
                label.FontFamily = UiTheme.UiFont;
                label.FontSize = UiTheme.SegmentFontSize;
                label.VerticalAlignment = VerticalAlignment.Center;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);

                segment.Child = label;
                _segments.Children.Add(segment);
                _labels.Add(label);
            }

            ApplyTheme();
        }

        internal void ApplyTheme()
        {
            _backgroundBrush.Color = UiTheme.BarBackground;

            for (int index = 0; index < _labels.Count && index < _visuals.Length; index++)
            {
                _labels[index].Foreground =
                    new SolidColorBrush(UiTheme.RoleColor(_visuals[index].Role));
            }
        }

        private void SetHovered(bool hovered)
        {
            if (_hovered == hovered)
            {
                return;
            }

            _hovered = hovered;
            DoubleAnimation animation = new DoubleAnimation(
                hovered ? 1.0 : 1.0 / 255.0,
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
