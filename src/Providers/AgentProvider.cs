using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// Claude Code 会话状态聚合。事件驱动：PollInterval = TimeSpan.MaxValue，
    /// 只有 EventServer 收到 Hook 或调度器显式 Refresh 时才生成快照。
    /// 不引用 System.Windows.*。
    /// </summary>
    internal sealed class AgentProvider : IProvider, IDisposable
    {
        internal const string ProviderIdValue = "agent";
        private const int DefaultStaleMinutes = 10;
        private const int DoneHoldSeconds = 30;

        private readonly object _sync = new object();
        private readonly Dictionary<string, SessionState> _sessions =
            new Dictionary<string, SessionState>(StringComparer.Ordinal);

        private int _staleMinutes;
        private bool _disposed;

        /// <summary>
        /// 事件到达后（后台线程）抛出；AppController 收到后调用
        /// ProviderScheduler.Refresh("agent")。不改变 IProvider 契约。
        /// </summary>
        internal event Action RefreshSuggested;

        internal AgentProvider()
            : this(DefaultStaleMinutes)
        {
        }

        internal AgentProvider(int staleMinutes)
        {
            _staleMinutes = NormalizeStaleMinutes(staleMinutes);
        }

        public string Id
        {
            get { return ProviderIdValue; }
        }

        public TimeSpan PollInterval
        {
            get { return TimeSpan.MaxValue; }
        }

        public TimeSpan Timeout
        {
            get { return TimeSpan.FromSeconds(5); }
        }

        internal void UpdateStaleMinutes(int staleMinutes)
        {
            lock (_sync)
            {
                _staleMinutes = NormalizeStaleMinutes(staleMinutes);
            }
        }

        /// <summary>
        /// 接收一条 Hook payload（已由 EventServer 校验为 JSON 对象）。
        /// 返回是否产生了会话状态变化。不含 session_id 的事件直接忽略。
        /// </summary>
        internal bool Ingest(Dictionary<string, object> payload)
        {
            if (payload == null)
            {
                return false;
            }

            string sessionId = GetString(payload, "session_id");
            if (string.IsNullOrEmpty(sessionId))
            {
                Log.Debug("忽略无 session_id 的 Hook 事件");
                return false;
            }

            string eventName = GetString(payload, "hook_event_name");
            string cwd = GetString(payload, "cwd");
            DateTime now = DateTime.UtcNow;
            bool changed = false;

            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                if (string.Equals(eventName, "SessionEnd", StringComparison.Ordinal))
                {
                    changed = _sessions.Remove(sessionId);
                }
                else
                {
                    SessionState state;
                    if (!_sessions.TryGetValue(sessionId, out state))
                    {
                        state = new SessionState();
                        state.SessionId = sessionId;
                        state.StartedUtc = now;
                        state.State = AgentState.Unknown;
                        state.Cwd = string.Empty;
                        state.ProjectName = string.Empty;
                        state.CurrentTool = string.Empty;
                        state.IsStale = false;
                        _sessions[sessionId] = state;
                    }

                    if (!string.IsNullOrEmpty(cwd))
                    {
                        state.Cwd = cwd;
                        state.ProjectName = CwdProjectName(cwd);
                    }

                    state.StaleMinutes = _staleMinutes;
                    state.LastEventUtc = now;
                    // 新事件到达即解除过期标记；下一次 Tick 会按新时间重算。
                    state.IsStale = false;
                    ApplyEvent(state, eventName, payload);
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseRefreshSuggested();
            }

            return changed;
        }

        /// <summary>
        /// 由 AppController 的 1s 定时器推进：Done 保持 30s 后回 Idle；
        /// 超过 staleMinutes 无事件标记 IsStale。返回是否发生变化。
        /// </summary>
        internal bool Tick(DateTime utcNow)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                utcNow = utcNow.ToUniversalTime();
            }

            bool changed = false;
            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                TimeSpan staleAfter = TimeSpan.FromMinutes(_staleMinutes);
                foreach (KeyValuePair<string, SessionState> pair in _sessions)
                {
                    SessionState state = pair.Value;
                    state.StaleMinutes = _staleMinutes;

                    if (state.State == AgentState.Done
                        && state.LastEventUtc != default(DateTime)
                        && (utcNow - state.LastEventUtc).TotalSeconds >= DoneHoldSeconds)
                    {
                        state.State = AgentState.Idle;
                        state.CurrentTool = string.Empty;
                        changed = true;
                    }

                    bool stale = state.LastEventUtc != default(DateTime)
                        && (utcNow - state.LastEventUtc) > staleAfter;
                    if (state.IsStale != stale)
                    {
                        state.IsStale = stale;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// 聚合快照：等待确认的会话优先，否则取 LastEventUtc 最大者。
        /// 无会话时返回 null。
        /// </summary>
        internal AgentSnapshot GetAggregateSnapshot()
        {
            lock (_sync)
            {
                if (_disposed || _sessions.Count == 0)
                {
                    return null;
                }

                SessionState selected = null;
                foreach (KeyValuePair<string, SessionState> pair in _sessions)
                {
                    SessionState state = pair.Value;
                    if (state.State != AgentState.WaitingPermission)
                    {
                        continue;
                    }

                    if (selected == null || state.LastEventUtc > selected.LastEventUtc)
                    {
                        selected = state;
                    }
                }

                if (selected == null)
                {
                    foreach (KeyValuePair<string, SessionState> pair in _sessions)
                    {
                        SessionState state = pair.Value;
                        if (selected == null || state.LastEventUtc > selected.LastEventUtc)
                        {
                            selected = state;
                        }
                    }
                }

                if (selected == null)
                {
                    return null;
                }

                return CreateSnapshot(selected, DateTime.UtcNow);
            }
        }

        /// <summary>
        /// 面板使用：返回所有会话的快照，按最后事件时间倒序。
        /// </summary>
        internal AgentSnapshot[] GetSessionSnapshots()
        {
            lock (_sync)
            {
                if (_disposed || _sessions.Count == 0)
                {
                    return new AgentSnapshot[0];
                }

                List<SessionState> states = new List<SessionState>(_sessions.Values);
                states.Sort(delegate(SessionState left, SessionState right)
                {
                    return right.LastEventUtc.CompareTo(left.LastEventUtc);
                });

                DateTime now = DateTime.UtcNow;
                AgentSnapshot[] snapshots = new AgentSnapshot[states.Count];
                for (int index = 0; index < states.Count; index++)
                {
                    snapshots[index] = CreateSnapshot(states[index], now);
                }

                return snapshots;
            }
        }

        public Task<ProviderResult> QueryAsync(CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }

            AgentSnapshot snapshot = GetAggregateSnapshot();

            ProviderResult result = new ProviderResult();
            result.ProviderId = ProviderIdValue;
            result.Snapshot = snapshot;
            result.Health = SnapshotHealth.Live;
            result.Error = string.Empty;
            result.UpdatedUtc = DateTime.UtcNow;
            return Task.FromResult<ProviderResult>(result);
        }

        private void ApplyEvent(
            SessionState state,
            string eventName,
            Dictionary<string, object> payload)
        {
            if (string.Equals(eventName, "SessionStart", StringComparison.Ordinal))
            {
                state.State = AgentState.Idle;
                state.CurrentTool = string.Empty;
                state.StartedUtc = DateTime.UtcNow;
                return;
            }

            if (string.Equals(eventName, "UserPromptSubmit", StringComparison.Ordinal))
            {
                state.State = AgentState.Thinking;
                state.CurrentTool = string.Empty;
                return;
            }

            if (string.Equals(eventName, "PreToolUse", StringComparison.Ordinal))
            {
                state.State = AgentState.Tool;
                string toolName = GetString(payload, "tool_name");
                state.CurrentTool = toolName == null ? string.Empty : toolName;
                return;
            }

            if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
            {
                state.State = AgentState.Thinking;
                state.CurrentTool = string.Empty;
                return;
            }

            if (string.Equals(eventName, "PermissionRequest", StringComparison.Ordinal))
            {
                state.State = AgentState.WaitingPermission;
                return;
            }

            if (string.Equals(eventName, "Notification", StringComparison.Ordinal))
            {
                ApplyNotification(state, payload);
                return;
            }

            if (string.Equals(eventName, "Stop", StringComparison.Ordinal))
            {
                state.State = AgentState.Done;
                return;
            }

            // 其他事件（含 SubagentStart/SubagentStop）：不改状态，
            // 仅由调用方更新 LastEventUtc。
        }

        private static void ApplyNotification(
            SessionState state,
            Dictionary<string, object> payload)
        {
            string notificationType = GetString(payload, "notification_type");
            if (!string.IsNullOrEmpty(notificationType))
            {
                if (string.Equals(
                    notificationType,
                    "permission_prompt",
                    StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        notificationType,
                        "agent_needs_input",
                        StringComparison.OrdinalIgnoreCase))
                {
                    state.State = AgentState.WaitingPermission;
                    return;
                }

                if (string.Equals(
                    notificationType,
                    "idle_prompt",
                    StringComparison.OrdinalIgnoreCase))
                {
                    state.State = AgentState.Idle;
                    state.CurrentTool = string.Empty;
                    return;
                }

                if (string.Equals(
                    notificationType,
                    "agent_completed",
                    StringComparison.OrdinalIgnoreCase))
                {
                    state.State = AgentState.Done;
                    return;
                }

                if (notificationType.StartsWith(
                    "quota_auto_resume",
                    StringComparison.OrdinalIgnoreCase))
                {
                    state.State = AgentState.WaitingQuota;
                    return;
                }

                // 未知 notification_type 不改变状态。
                return;
            }

            string message = GetString(payload, "message");
            if (ContainsAny(message, "permission")
                || ContainsAny(message, "approve")
                || ContainsAny(message, "allow"))
            {
                state.State = AgentState.WaitingPermission;
                return;
            }

            state.State = AgentState.Unknown;
        }

        private static AgentSnapshot CreateSnapshot(SessionState state, DateTime now)
        {
            AgentSnapshot snapshot = new AgentSnapshot();
            snapshot.SessionId = state.SessionId;
            snapshot.ProjectName = state.ProjectName == null
                ? string.Empty
                : state.ProjectName;
            snapshot.Cwd = state.Cwd == null ? string.Empty : state.Cwd;
            snapshot.State = state.State;
            snapshot.CurrentTool = state.CurrentTool;
            snapshot.Model = null;
            snapshot.ContextUsedPercent = null;
            snapshot.StartedUtc = state.StartedUtc;
            snapshot.LastEventUtc = state.LastEventUtc;
            snapshot.IsStale = state.IsStale
                || (state.LastEventUtc != default(DateTime)
                    && (now - state.LastEventUtc)
                        > TimeSpan.FromMinutes(NormalizeStaleMinutes(state.StaleMinutes)));
            snapshot.UpdatedUtc = now;
            snapshot.Health = SnapshotHealth.Live;
            snapshot.Error = string.Empty;
            return snapshot;
        }

        private void RaiseRefreshSuggested()
        {
            Action handler = RefreshSuggested;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler();
            }
            catch (Exception exception)
            {
                Log.Write("Agent 刷新事件处理失败：" + exception.GetType().Name);
            }
        }

        private static string GetString(Dictionary<string, object> payload, string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            return value as string;
        }

        private static bool ContainsAny(string text, string keyword)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
            {
                return false;
            }

            return text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string CwdProjectName(string cwd)
        {
            if (string.IsNullOrEmpty(cwd))
            {
                return string.Empty;
            }

            string trimmed = cwd.TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return cwd;
            }

            if (trimmed.Length == 2 && trimmed[1] == ':')
            {
                return trimmed;
            }

            try
            {
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? trimmed : name;
            }
            catch (ArgumentException)
            {
                return trimmed;
            }
        }

        private static int NormalizeStaleMinutes(int minutes)
        {
            if (minutes <= 0)
            {
                return DefaultStaleMinutes;
            }

            return minutes;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _sessions.Clear();
            }
        }

        private sealed class SessionState
        {
            internal string SessionId;
            internal string Cwd;
            internal string ProjectName;
            internal AgentState State;
            internal string CurrentTool;
            internal DateTime StartedUtc;
            internal DateTime LastEventUtc;
            internal int StaleMinutes;
            internal bool IsStale;
        }
    }
}
