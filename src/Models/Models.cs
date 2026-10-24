using System;

namespace VibeBar
{
    internal enum SegmentKind
    {
        Quota,
        Git,
        Agent
    }

    internal enum SnapshotSource
    {
        Official,
        LocalLog,
        Experimental,
        None
    }

    internal enum SnapshotHealth
    {
        Live,
        Stale,
        Error,
        Unavailable
    }

    internal enum AgentState
    {
        Idle,
        Thinking,
        Tool,
        WaitingPermission,
        WaitingQuota,
        Done,
        Error,
        Unknown
    }

    internal enum ColorRole
    {
        Neutral,
        Accent,
        Warning,
        Critical,
        Waiting,
        Success
    }

    internal sealed class QuotaSnapshot
    {
        public string ProviderId;
        public string DisplayName;
        public decimal? Balance;
        public string Currency;
        public decimal? GrantedBalance;
        public decimal? ToppedUpBalance;
        public bool IsAvailable;
        public bool IsOffPeak;
        public DateTime NextTransitionUtc;
        public DateTime UpdatedUtc;
        public SnapshotHealth Health;
        public SnapshotSource Source;
        public string Error;
    }

    internal sealed class GitSnapshot
    {
        public string RepoPath;
        public string RepoName;
        public string Branch;
        public bool IsDetached;
        public int StagedCount;
        public int ModifiedCount;
        public int UntrackedCount;
        public int ConflictCount;
        public int Ahead;
        public int Behind;
        public string Operation;
        public DateTime UpdatedUtc;
        public SnapshotHealth Health;
        public string Error;

        public int DirtyCount
        {
            get { return StagedCount + ModifiedCount + UntrackedCount; }
        }
    }

    internal sealed class AgentSnapshot
    {
        public string SessionId;
        public string ProjectName;
        public string Cwd;
        public AgentState State;
        public string CurrentTool;
        public string Model;
        public double? ContextUsedPercent;
        public DateTime StartedUtc;
        public DateTime LastEventUtc;
        public bool IsStale;
        public DateTime UpdatedUtc;
        public SnapshotHealth Health;
        public string Error;
    }

    internal sealed class ProviderResult
    {
        public string ProviderId;
        public object Snapshot;
        public SnapshotHealth Health;
        public string Error;
        public DateTime UpdatedUtc;
    }
}
