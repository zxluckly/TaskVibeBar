namespace VibeBar
{
    /// <summary>
    /// 段描述：供设置窗口动态生成模块开关，新增信息显示时只需注册新的 Presenter。
    /// </summary>
    internal sealed class SegmentDescriptor
    {
        internal readonly string Id;
        internal readonly string DisplayName;
        internal readonly int DefaultPriority;

        internal SegmentDescriptor(string id, string displayName, int defaultPriority)
        {
            Id = id;
            DisplayName = displayName;
            DefaultPriority = defaultPriority;
        }
    }
}
