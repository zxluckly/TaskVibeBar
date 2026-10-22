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
        internal const double SegmentFontSize = 13.0;
        internal const double PanelWidth = 360.0;
        internal const double PanelMaxHeight = 480.0;

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
