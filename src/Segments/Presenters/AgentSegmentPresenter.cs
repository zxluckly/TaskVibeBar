namespace VibeBar
{
    internal sealed class AgentSegmentPresenter : SegmentPresenterBase
    {
        internal const string SegmentId = "agent";

        private AgentSnapshot _aggregate;
        private AgentSnapshot[] _sessions = new AgentSnapshot[0];

        internal AgentSegmentPresenter()
            : base(SegmentId, "Claude 会话", 30)
        {
        }

        internal void Update(AgentSnapshot aggregate, AgentSnapshot[] sessions)
        {
            _aggregate = aggregate;
            _sessions = sessions == null ? new AgentSnapshot[0] : sessions;
        }

        internal AgentSnapshot Aggregate
        {
            get { return _aggregate; }
        }

        internal AgentSnapshot[] Sessions
        {
            get { return _sessions; }
        }

        public override SegmentVisual BuildVisual(bool privacy)
        {
            return SegmentFactory.CreateAgent(_aggregate, _sessions, privacy);
        }

        public override DetailSection BuildDetail(bool privacy)
        {
            if (_sessions == null || _sessions.Length == 0)
            {
                return null;
            }

            DetailSection section = SegmentFactory.CreateAgentDetail(_sessions, privacy);
            section.Actions = new DetailAction[0];
            return section;
        }
    }
}
