using System;

namespace VibeBar
{
    /// <summary>
    /// 任务栏 UI 唯一接受的段视图模型。Provider 层不得直接产生该类型之外的 UI 数据。
    /// </summary>
    internal sealed class SegmentVisual
    {
        public SegmentKind Kind;
        public string Glyph;
        public string Text;
        public string Tooltip;
        public ColorRole Role;
        public bool Pulse;
        public int Priority;
    }

    internal sealed class DetailSection
    {
        public string Title;
        public string[] Lines;
        public DetailAction[] Actions;
    }

    internal sealed class DetailAction
    {
        public string Label;
        public Action Handler;
    }
}
