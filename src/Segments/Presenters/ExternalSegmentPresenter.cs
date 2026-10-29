using System;
using System.Collections.Generic;

namespace VibeBar
{
    /// <summary>
    /// 外部段 Presenter（插件协议 v1）。一段一个实例，段 Id 为 "ext:" + 推送 id，
    /// 与内置段 id 隔离。内容全部来自 ExternalSegmentState，本类不写死任何业务文案。
    /// </summary>
    internal sealed class ExternalSegmentPresenter : SegmentPresenterBase
    {
        internal const string SegmentIdPrefix = "ext:";

        private readonly ISegmentActionHost _actions;
        private ExternalSegmentState _state;

        internal ExternalSegmentPresenter(
            ExternalSegmentState state,
            ISegmentActionHost actions)
            : base(
                SegmentIdOf(state == null ? null : state.Id),
                DisplayNameOf(state),
                state == null ? SegmentProtocol.DefaultPriority : state.Priority)
        {
            _state = state;
            _actions = actions;
        }

        internal static string SegmentIdOf(string externalId)
        {
            return SegmentIdPrefix + (externalId == null ? string.Empty : externalId);
        }

        internal ExternalSegmentState State
        {
            get { return _state; }
        }

        /// <summary>
        /// 同一 id 的续推：只替换状态，不重建实例。
        /// </summary>
        internal void Update(ExternalSegmentState state)
        {
            _state = state;
        }

        internal bool IsExpired(DateTime nowUtc)
        {
            return _state == null || _state.ExpiresUtc <= nowUtc;
        }

        // 外部段的排序完全由推送的 priority 决定。
        public override int DefaultPriority
        {
            get
            {
                return _state == null
                    ? SegmentProtocol.DefaultPriority
                    : _state.Priority;
            }
        }

        public override bool ShowInSettings
        {
            get { return false; }
        }

        /// <summary>
        /// 外部段只受 external.enabled 总开关控制，不查 ui.segments。
        /// </summary>
        public override bool IsEnabled(AppSettings settings)
        {
            return settings == null
                || settings.External == null
                || settings.External.Enabled;
        }

        public override SegmentVisual BuildVisual(bool privacy)
        {
            // text 为空字符串表示「隐藏但保留 id」，后续 set 可恢复。
            if (_state == null || string.IsNullOrEmpty(_state.Text))
            {
                return null;
            }

            return SegmentFactory.CreateExternal(_state, DisplayName);
        }

        public override DetailSection BuildDetail(bool privacy)
        {
            if (_state == null || string.IsNullOrEmpty(_state.Text))
            {
                return null;
            }

            DetailSection section = SegmentFactory.CreateExternalDetail(
                _state,
                DisplayName);
            if (_actions == null)
            {
                section.Actions = new DetailAction[0];
                return section;
            }

            List<DetailAction> actions = new List<DetailAction>();

            if (!string.IsNullOrEmpty(_state.Url))
            {
                string url = _state.Url;
                actions.Add(CreateAction(
                    "打开链接",
                    delegate { _actions.OpenUrl(url); }));
            }

            string text = _state.Text;
            DetailAction copy = new DetailAction();
            copy.Label = "复制文本";
            if (!string.IsNullOrEmpty(text))
            {
                copy.Handler = delegate { _actions.CopyText(text); };
            }

            actions.Add(copy);

            section.Actions = actions.ToArray();
            return section;
        }

        private static string DisplayNameOf(ExternalSegmentState state)
        {
            if (state == null)
            {
                return string.Empty;
            }

            return string.IsNullOrEmpty(state.Label) ? state.Id : state.Label;
        }
    }
}
