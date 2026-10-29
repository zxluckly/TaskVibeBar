using System;

namespace VibeBar
{
    /// <summary>
    /// 一个可显示的段。新增信息显示时：
    /// 1) 实现 IProvider 取数；2) 实现 ISegmentPresenter 出 UI；
    /// 3) 在 AppController.CreateUi 中注册；4) 设置窗口会自动出现开关。
    /// </summary>
    internal interface ISegmentPresenter
    {
        string Id { get; }
        string DisplayName { get; }
        int DefaultPriority { get; }

        /// <summary>
        /// 新增段首次进入 ui.segments 时的默认显隐。P1 可选段（端口、PR）默认关闭。
        /// </summary>
        bool DefaultEnabled { get; }

        bool IsEnabled(AppSettings settings);
        SegmentVisual BuildVisual(bool privacy);
        DetailSection BuildDetail(bool privacy);
    }

    /// <summary>
    /// Presenter 通过它执行面板按钮动作，避免直接依赖 AppController。
    /// </summary>
    internal interface ISegmentActionHost
    {
        void RefreshProvider(string providerId);
        void OpenUrl(string url);
        void OpenFolder(string path);
        void CopyText(string text);
        void ShowSettings();
    }

    internal abstract class SegmentPresenterBase : ISegmentPresenter
    {
        private readonly string _id;
        private readonly string _displayName;
        private readonly int _defaultPriority;

        protected SegmentPresenterBase(string id, string displayName, int defaultPriority)
        {
            _id = id;
            _displayName = displayName;
            _defaultPriority = defaultPriority;
        }

        public string Id
        {
            get { return _id; }
        }

        public string DisplayName
        {
            get { return _displayName; }
        }

        public int DefaultPriority
        {
            get { return _defaultPriority; }
        }

        public virtual bool DefaultEnabled
        {
            get { return true; }
        }

        public virtual bool IsEnabled(AppSettings settings)
        {
            return settings == null
                || settings.Ui == null
                || settings.Ui.IsSegmentEnabled(_id);
        }

        public abstract SegmentVisual BuildVisual(bool privacy);

        public abstract DetailSection BuildDetail(bool privacy);

        protected static DetailAction CreateAction(string label, Action handler)
        {
            DetailAction action = new DetailAction();
            action.Label = label;
            action.Handler = handler;
            return action;
        }
    }
}
