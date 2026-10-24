namespace VibeBar
{
    internal sealed class QuotaSegmentPresenter : SegmentPresenterBase
    {
        internal const string SegmentId = "quota";
        private const string UsageUrl = "https://platform.deepseek.com/usage";

        private readonly ISegmentActionHost _actions;
        private QuotaSnapshot _snapshot;

        internal QuotaSegmentPresenter(ISegmentActionHost actions)
            : base(SegmentId, "额度", 10)
        {
            _actions = actions;
        }

        internal void Update(QuotaSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        internal QuotaSnapshot Snapshot
        {
            get { return _snapshot; }
        }

        public override SegmentVisual BuildVisual(bool privacy)
        {
            return SegmentFactory.CreateQuota(_snapshot, privacy);
        }

        public override DetailSection BuildDetail(bool privacy)
        {
            if (_snapshot == null)
            {
                return null;
            }

            DetailSection section = SegmentFactory.CreateQuotaDetail(_snapshot, privacy);
            if (_actions == null)
            {
                section.Actions = new DetailAction[0];
                return section;
            }

            section.Actions = new DetailAction[]
            {
                CreateAction("打开用量页", delegate { _actions.OpenUrl(UsageUrl); }),
                CreateAction("刷新", delegate
                {
                    _actions.RefreshProvider(DeepSeekQuotaProvider.ProviderIdValue);
                })
            };
            return section;
        }
    }
}
