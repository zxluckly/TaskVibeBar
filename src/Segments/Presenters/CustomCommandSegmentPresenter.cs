using System;
using System.Collections.Generic;

namespace VibeBar
{
    /// <summary>
    /// 自定义命令段 Presenter。段 Id 直接使用配置中的命令 id，
    /// Provider Id 为 "cmd:" + id，由 AppController 在结果分发时拼装。
    /// </summary>
    internal sealed class CustomCommandSegmentPresenter : SegmentPresenterBase
    {
        private readonly ISegmentActionHost _actions;
        private readonly string _command;
        private readonly string _url;
        private CustomCommandSnapshot _snapshot;

        internal CustomCommandSegmentPresenter(
            string id,
            string label,
            string command,
            string url,
            int priority,
            ISegmentActionHost actions)
            : base(id, string.IsNullOrEmpty(label) ? id : label, priority)
        {
            _actions = actions;
            _command = command;
            _url = url;
        }

        internal void Update(CustomCommandSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        internal CustomCommandSnapshot Snapshot
        {
            get { return _snapshot; }
        }

        public override SegmentVisual BuildVisual(bool privacy)
        {
            return SegmentFactory.CreateCustom(_snapshot, DisplayName);
        }

        public override DetailSection BuildDetail(bool privacy)
        {
            DetailSection section = SegmentFactory.CreateCustomDetail(
                _snapshot,
                DisplayName,
                _command);
            if (_actions == null)
            {
                section.Actions = new DetailAction[0];
                return section;
            }

            List<DetailAction> actions = new List<DetailAction>();

            DetailAction refresh = new DetailAction();
            refresh.Label = "立即刷新";
            string providerId = CustomCommandProvider.ProviderIdPrefix + Id;
            refresh.Handler = delegate { _actions.RefreshProvider(providerId); };
            actions.Add(refresh);

            string target = _url;
            if (string.IsNullOrEmpty(target) && _snapshot != null)
            {
                target = _snapshot.Url;
            }

            DetailAction open = new DetailAction();
            open.Label = "打开链接";
            if (!string.IsNullOrEmpty(target))
            {
                string capturedUrl = target;
                open.Handler = delegate { _actions.OpenUrl(capturedUrl); };
            }

            actions.Add(open);

            DetailAction copy = new DetailAction();
            copy.Label = "复制输出";
            if (_snapshot != null && !string.IsNullOrEmpty(_snapshot.Text))
            {
                string capturedText = _snapshot.Text;
                copy.Handler = delegate { _actions.CopyText(capturedText); };
            }

            actions.Add(copy);

            section.Actions = actions.ToArray();
            return section;
        }
    }
}
