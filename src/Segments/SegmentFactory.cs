namespace VibeBar
{
    /// <summary>
    /// T1 只产生三个静态占位段。后续任务在此接入快照，禁止 Provider 直接接触 UI。
    /// </summary>
    internal static class SegmentFactory
    {
        internal static SegmentVisual[] CreatePlaceholders()
        {
            SegmentVisual[] visuals = new SegmentVisual[3];
            visuals[0] = Create(SegmentKind.Quota, "额度 —", "额度", 10);
            visuals[1] = Create(SegmentKind.Git, "Git —", "Git", 20);
            visuals[2] = Create(SegmentKind.Agent, "会话 —", "会话", 30);
            return visuals;
        }

        private static SegmentVisual Create(SegmentKind kind, string text, string tooltip, int priority)
        {
            SegmentVisual visual = new SegmentVisual();
            visual.Kind = kind;
            visual.Glyph = string.Empty;
            visual.Text = text;
            visual.Tooltip = tooltip;
            visual.Role = ColorRole.Neutral;
            visual.Pulse = false;
            visual.Priority = priority;
            return visual;
        }
    }
}
