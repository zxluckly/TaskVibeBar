using System;

namespace VibeBar
{
    internal enum SegmentKind
    {
        Quota = 0,
        Git = 1,
        Agent = 2,
        Port = 3,
        Pr = 4,
        Custom = 5
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

    /// <summary>
    /// 一个监听中的本地端口。Provider 只填数据，不涉及展示格式。
    /// </summary>
    internal sealed class PortEntry
    {
        public int Port;
        public int ProcessId;
        public string ProcessName;
    }

    internal sealed class PortSnapshot
    {
        public PortEntry[] Ports;
        public int[] WatchPorts;
        public bool WatchAll;
        public DateTime UpdatedUtc;
        public SnapshotHealth Health;
        public string Error;

        public int Count
        {
            get { return Ports == null ? 0 : Ports.Length; }
        }
    }

    /// <summary>
    /// 当前仓库的 PR / CI 快照。Exists=false 表示没有打开的 PR（该段隐藏）。
    /// </summary>
    internal sealed class PrSnapshot
    {
        public int Number;
        public string Title;
        public string State;
        public bool IsDraft;
        public string ReviewDecision;
        public int ChecksPassed;
        public int ChecksFailed;
        public int ChecksPending;
        public string Url;
        public bool Exists;
        public DateTime UpdatedUtc;
        public SnapshotHealth Health;
        public string Error;
    }

    /// <summary>
    /// 自定义命令段的输出。E1 协议：命令 stdout → JSON {text, tooltip, color, url}。
    /// </summary>
    internal sealed class CustomCommandSnapshot
    {
        public string Id;
        public string Label;
        public string Text;
        public string Tooltip;
        public ColorRole Role;
        public string Url;
        public int DurationMilliseconds;
        public int ExitCode;
        public DateTime UpdatedUtc;
        public SnapshotHealth Health;
        public string Error;
    }
}
