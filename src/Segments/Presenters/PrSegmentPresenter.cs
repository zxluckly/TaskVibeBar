using System;
using System.Collections.Generic;
using System.Globalization;

namespace VibeBar
{
    /// <summary>
    /// PR / CI 段：数据来自 `gh pr view --json`。没有打开的 PR 时不显示该段。
    /// </summary>
    internal sealed class PrSegmentPresenter : SegmentPresenterBase
    {
        internal const string SegmentId = "pr";

        private readonly ISegmentActionHost _actions;
        private PrSnapshot _snapshot;

        internal PrSegmentPresenter(ISegmentActionHost actions)
            : base(SegmentId, "PR / CI", 50)
        {
            _actions = actions;
        }

        internal void Update(PrSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        internal PrSnapshot Snapshot
        {
            get { return _snapshot; }
        }

        public override SegmentVisual BuildVisual(bool privacy)
        {
            return SegmentFactory.CreatePr(_snapshot);
        }

        public override DetailSection BuildDetail(bool privacy)
        {
            if (_snapshot == null || (!_snapshot.Exists
                && _snapshot.Health != SnapshotHealth.Error))
            {
                return null;
            }

            DetailSection section = SegmentFactory.CreatePrDetail(_snapshot);
            if (_actions == null)
            {
                section.Actions = new DetailAction[0];
                return section;
            }

            List<DetailAction> actions = new List<DetailAction>();

            DetailAction open = new DetailAction();
            open.Label = "打开 PR";
            string url = _snapshot.Url;
            if (!string.IsNullOrEmpty(url))
            {
                string capturedUrl = url;
                open.Handler = delegate { _actions.OpenUrl(capturedUrl); };
            }

            actions.Add(open);

            DetailAction copy = new DetailAction();
            copy.Label = "复制链接";
            if (!string.IsNullOrEmpty(url))
            {
                string capturedUrl = url;
                copy.Handler = delegate { _actions.CopyText(capturedUrl); };
            }

            actions.Add(copy);
            section.Actions = actions.ToArray();
            return section;
        }
    }
}
