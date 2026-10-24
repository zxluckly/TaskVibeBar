namespace VibeBar
{
    internal sealed class GitSegmentPresenter : SegmentPresenterBase
    {
        internal const string SegmentId = "git";

        private readonly ISegmentActionHost _actions;
        private GitSnapshot _snapshot;

        internal GitSegmentPresenter(ISegmentActionHost actions)
            : base(SegmentId, "Git", 20)
        {
            _actions = actions;
        }

        internal void Update(GitSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        internal GitSnapshot Snapshot
        {
            get { return _snapshot; }
        }

        public override SegmentVisual BuildVisual(bool privacy)
        {
            return SegmentFactory.CreateGit(_snapshot, privacy);
        }

        public override DetailSection BuildDetail(bool privacy)
        {
            if (_snapshot == null)
            {
                return null;
            }

            DetailSection section = SegmentFactory.CreateGitDetail(_snapshot, privacy);
            if (_actions == null)
            {
                section.Actions = new DetailAction[0];
                return section;
            }

            DetailAction openFolder = new DetailAction();
            openFolder.Label = "打开目录";
            string repoPath = _snapshot == null ? null : _snapshot.RepoPath;
            if (!string.IsNullOrEmpty(repoPath))
            {
                string capturedPath = repoPath;
                openFolder.Handler = delegate { _actions.OpenFolder(capturedPath); };
            }

            DetailAction copyBranch = new DetailAction();
            copyBranch.Label = "复制分支";
            string branch = _snapshot == null ? null : _snapshot.Branch;
            if (!string.IsNullOrEmpty(branch)
                && _snapshot != null
                && !_snapshot.IsDetached)
            {
                string capturedBranch = branch;
                copyBranch.Handler = delegate { _actions.CopyText(capturedBranch); };
            }

            section.Actions = new DetailAction[] { openFolder, copyBranch };
            return section;
        }
    }
}
