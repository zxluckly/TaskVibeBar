using System.Windows;
using System.Windows.Media;

namespace VibeBar
{
    internal static class UiTheme
    {
        internal const double CornerRadiusValue = 8.0;
        internal const double SegmentSpacing = 6.0;
        internal const double BarContentHeight = 32.0;
        internal const double BarVerticalPadding = 1.0;
        internal const double BarHorizontalPadding = 10.0;
        internal const double SegmentFontSize = 13.0;
        internal const double SegmentHorizontalPadding = 4.0;
        internal const double SegmentCornerRadius = 5.0;
        internal const double SeparatorWidth = 1.0;
        internal const double SeparatorSpacing = 6.0;
        internal const double SeparatorHeight = 14.0;
        internal const double PanelWidth = 360.0;
        internal const double PanelMaxHeight = 640.0;
        internal const double PanelCornerRadius = 10.0;
        internal const double PanelShadowMargin = 8.0;

        internal static readonly FontFamily UiFont = new FontFamily("Segoe UI");

        internal static Color TextPrimary
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(255, 27, 27, 27)
                    : Color.FromArgb(255, 242, 242, 242);
            }
        }

        internal static Color TextSecondary
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(255, 95, 95, 95)
                    : Color.FromArgb(255, 158, 158, 158);
            }
        }

        internal static Color BarBackground
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(235, 255, 255, 255)
                    : Color.FromArgb(235, 32, 32, 32);
            }
        }

        internal static Color PanelBackground
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(235, 255, 255, 255)
                    : Color.FromArgb(235, 32, 32, 32);
            }
        }

        internal static Color Border
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(52, 0, 0, 0)
                    : Color.FromArgb(58, 255, 255, 255);
            }
        }

        internal static Color Separator
        {
            get
            {
                // 约 12% 不透明度，浅色用黑、深色用白，保持细而不刺眼。
                return Theme.IsLight()
                    ? Color.FromArgb(31, 0, 0, 0)
                    : Color.FromArgb(31, 255, 255, 255);
            }
        }

        internal static Color SegmentHover
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(22, 0, 0, 0)
                    : Color.FromArgb(32, 255, 255, 255);
            }
        }

        internal static Color ButtonBackground
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(18, 0, 0, 0)
                    : Color.FromArgb(26, 255, 255, 255);
            }
        }

        internal static Color ButtonHover
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(32, 0, 0, 0)
                    : Color.FromArgb(42, 255, 255, 255);
            }
        }

        internal static Color ButtonPressed
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(48, 0, 0, 0)
                    : Color.FromArgb(60, 255, 255, 255);
            }
        }

        internal static Color TextDisabled
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(255, 166, 166, 166)
                    : Color.FromArgb(255, 108, 108, 108);
            }
        }

        internal static Color MenuBackground
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(255, 247, 247, 247)
                    : Color.FromArgb(255, 32, 32, 32);
            }
        }

        internal static Color MenuHover
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(255, 228, 228, 228)
                    : Color.FromArgb(255, 60, 60, 60);
            }
        }

        internal static Color MenuBorder
        {
            get
            {
                return Theme.IsLight()
                    ? Color.FromArgb(255, 200, 200, 200)
                    : Color.FromArgb(255, 66, 66, 66);
            }
        }

        internal static Color RoleColor(ColorRole role)
        {
            bool light = Theme.IsLight();
            if (role == ColorRole.Accent)
            {
                return light
                    ? Color.FromArgb(255, 59, 130, 246)
                    : Color.FromArgb(255, 96, 165, 250);
            }

            if (role == ColorRole.Warning)
            {
                return light
                    ? Color.FromArgb(255, 178, 106, 0)
                    : Color.FromArgb(255, 255, 196, 84);
            }

            if (role == ColorRole.Critical)
            {
                return light
                    ? Color.FromArgb(255, 198, 40, 40)
                    : Color.FromArgb(255, 255, 108, 108);
            }

            if (role == ColorRole.Waiting)
            {
                return light
                    ? Color.FromArgb(255, 47, 111, 235)
                    : Color.FromArgb(255, 96, 165, 250);
            }

            if (role == ColorRole.Success)
            {
                return light
                    ? Color.FromArgb(255, 30, 142, 62)
                    : Color.FromArgb(255, 112, 222, 142);
            }

            return TextPrimary;
        }

        internal static SolidColorBrush CreateBrush(Color color)
        {
            return new SolidColorBrush(color);
        }
    }
}
