namespace VibeBar
{
    /// <summary>
    /// 段描述：供设置窗口动态生成模块开关，新增信息显示时只需注册新的 Presenter。
    /// DefaultEnabled 决定该段首次进入 ui.segments 时是否默认开启。
    /// </summary>
    internal sealed class SegmentDescriptor
    {
        internal readonly string Id;
        internal readonly string DisplayName;
        internal readonly int DefaultPriority;
        internal readonly bool DefaultEnabled;

        internal SegmentDescriptor(string id, string displayName, int defaultPriority)
            : this(id, displayName, defaultPriority, true)
        {
        }

        internal SegmentDescriptor(
            string id,
            string displayName,
            int defaultPriority,
            bool defaultEnabled)
        {
            Id = id;
            DisplayName = displayName;
            DefaultPriority = defaultPriority;
            DefaultEnabled = defaultEnabled;
        }
    }
}
