using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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

                    string transcriptPath = GetString(payload, "transcript_path");
                    if (!string.IsNullOrEmpty(transcriptPath))
                    {
                        state.TranscriptPath = transcriptPath;
                    }

                    state.StaleMinutes = _staleMinutes;
                    state.LastEventUtc = now;
                    // 新事件到达即解除过期标记；下一次 Tick 会按新时间重算。
                    state.IsStale = false;
                    ApplyEvent(state, eventName, payload);

                    // statusLine 未提供上下文时，从会话转录兜底读取模型/上下文。
                    if (TryRefreshTranscriptContext(state, now))
                    {
                        changed = true;
                    }

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
        /// 接收 Claude Code statusLine 的 JSON：只更新模型/上下文/cwd，不改变会话状态。
        /// 会话不存在时创建为空闲会话（用于只安装 statusLine、未装 Hooks 的场景）。
        /// </summary>
        internal bool IngestStatusLine(Dictionary<string, object> payload)
        {
            if (payload == null)
            {
                return false;
            }

            string sessionId = GetString(payload, "session_id");
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }

            string cwd = GetString(payload, "cwd");
            Dictionary<string, object> workspace = GetObject(payload, "workspace");
            if (string.IsNullOrEmpty(cwd) && workspace != null)
            {
                cwd = GetString(workspace, "current_dir");
                if (string.IsNullOrEmpty(cwd))
                {
                    cwd = GetString(workspace, "project_dir");
                }
            }

            Dictionary<string, object> model = GetObject(payload, "model");
            string modelName = model == null ? null : GetString(model, "display_name");
            if (string.IsNullOrEmpty(modelName) && model != null)
            {
                modelName = GetString(model, "id");
            }

            Dictionary<string, object> context = GetObject(payload, "context_window");
            double? usedPercent = null;
            if (context != null)
            {
                usedPercent = GetDouble(context, "used_percentage");
                if (!usedPercent.HasValue)
                {
                    usedPercent = GetDouble(context, "used_percent");
                }
            }

            DateTime now = DateTime.UtcNow;
            bool meaningfulChange = false;

            lock (_sync)
            {
                if (_disposed)
                {
                    return false;
                }

                SessionState state;
                if (!_sessions.TryGetValue(sessionId, out state))
                {
                    state = new SessionState();
                    state.SessionId = sessionId;
                    state.StartedUtc = now;
                    state.State = AgentState.Idle;
                    state.Cwd = string.Empty;
                    state.ProjectName = string.Empty;
                    state.CurrentTool = string.Empty;
                    _sessions[sessionId] = state;
                    meaningfulChange = true;
                }

                if (!string.IsNullOrEmpty(cwd)
                    && !string.Equals(state.Cwd, cwd, StringComparison.Ordinal))
                {
                    state.Cwd = cwd;
                    state.ProjectName = CwdProjectName(cwd);
                    meaningfulChange = true;
                }

                if (!string.IsNullOrEmpty(modelName)
                    && !string.Equals(state.Model, modelName, StringComparison.Ordinal))
                {
                    state.Model = modelName;
                    meaningfulChange = true;
                }

                if (usedPercent.HasValue)
                {
                    double value = usedPercent.Value;
                    if (value < 0)
                    {
                        value = 0;
                    }
                    else if (value > 100)
                    {
                        value = 100;
                    }

                    // statusLine 刷新很频繁；只有四舍五入后的百分比变化才触发 UI 刷新。
                    double rounded = Math.Round(value);
                    double previous = state.ContextUsedPercent.HasValue
                        ? Math.Round(state.ContextUsedPercent.Value)
                        : -1;
                    if (Math.Abs(rounded - previous) >= 0.5)
                    {
                        state.ContextUsedPercent = value;
                        state.ContextFromStatusLine = true;
                        meaningfulChange = true;
                    }
                    else if (!state.ContextUsedPercent.HasValue)
                    {
                        state.ContextUsedPercent = value;
                        state.ContextFromStatusLine = true;
                        meaningfulChange = true;
                    }
                }

                state.StaleMinutes = _staleMinutes;
                state.LastEventUtc = now;
            }

            if (meaningfulChange)
            {
                RaiseRefreshSuggested();
            }

            return meaningfulChange;
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
            snapshot.Model = state.Model;
            snapshot.ContextUsedPercent = state.ContextUsedPercent;
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

        private static bool TryRefreshTranscriptContext(
            SessionState state,
            DateTime now)
        {
            if (state == null
                || state.ContextFromStatusLine
                || string.IsNullOrEmpty(state.TranscriptPath))
            {
                return false;
            }

            if (state.LastTranscriptReadUtc != default(DateTime)
                && (now - state.LastTranscriptReadUtc).TotalSeconds < 2)
            {
                return false;
            }

            state.LastTranscriptReadUtc = now;

            try
            {
                using (FileStream stream = new FileStream(
                    state.TranscriptPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long length = stream.Length;
                    int take = (int)Math.Min(length, 512L * 1024L);
                    if (take <= 0)
                    {
                        return false;
                    }

                    stream.Seek(-take, SeekOrigin.End);
                    byte[] buffer = new byte[take];
                    int read = 0;
                    while (read < take)
                    {
                        int count = stream.Read(buffer, read, take - read);
                        if (count <= 0)
                        {
                            break;
                        }

                        read += count;
                    }

                    string text = Encoding.UTF8.GetString(buffer, 0, read);
                    string[] lines = text.Split('\n');
                    for (int index = lines.Length - 1; index >= 0; index--)
                    {
                        string line = lines[index].Trim();
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        // 文件末尾可能正在写入半行，跳过它。
                        if (index == 0 && take < length)
                        {
                            continue;
                        }

                        Dictionary<string, object> entry;
                        try
                        {
                            entry = Json.Deserialize<Dictionary<string, object>>(line);
                        }
                        catch
                        {
                            continue;
                        }

                        if (entry == null
                            || !string.Equals(
                                GetString(entry, "type"),
                                "assistant",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        Dictionary<string, object> message = GetObject(entry, "message");
                        if (message == null)
                        {
                            continue;
                        }

                        Dictionary<string, object> usage = GetObject(message, "usage");
                        if (usage == null)
                        {
                            continue;
                        }

                        string model = GetString(message, "model");
                        if (string.IsNullOrEmpty(model))
                        {
                            model = GetString(entry, "requestedModel");
                        }

                        double input = GetDouble(usage, "input_tokens") ?? 0;
                        double cacheRead = GetDouble(usage, "cache_read_input_tokens") ?? 0;
                        double cacheCreate = GetDouble(usage, "cache_creation_input_tokens") ?? 0;
                        double tokens = input + cacheRead + cacheCreate;
                        if (tokens <= 0)
                        {
                            continue;
                        }

                        double window = ContextWindowFor(model);
                        double percent = Math.Min(100.0, tokens / window * 100.0);

                        bool changed = false;
                        if (!string.IsNullOrEmpty(model)
                            && !string.Equals(state.Model, model, StringComparison.Ordinal))
                        {
                            state.Model = model;
                            changed = true;
                        }

                        double rounded = Math.Round(percent);
                        double previous = state.ContextUsedPercent.HasValue
                            ? Math.Round(state.ContextUsedPercent.Value)
                            : -1;
                        if (Math.Abs(rounded - previous) >= 0.5)
                        {
                            state.ContextUsedPercent = percent;
                            changed = true;
                        }

                        return changed;
                    }
                }
            }
            catch
            {
                // 转录文件可能被占用/格式变化；失败时静默，等待 statusLine 或下次事件。
            }

            return false;
        }

        private static double ContextWindowFor(string model)
        {
            string name = model == null ? string.Empty : model.ToLowerInvariant();
            if (name.IndexOf("1m", StringComparison.Ordinal) >= 0
                || name.IndexOf("deepseek-v4", StringComparison.Ordinal) >= 0)
            {
                return 1000000.0;
            }

            if (name.IndexOf("200k", StringComparison.Ordinal) >= 0)
            {
                return 200000.0;
            }

            return 200000.0;
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

        private static Dictionary<string, object> GetObject(
            Dictionary<string, object> payload,
            string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            return value as Dictionary<string, object>;
        }

        private static double? GetDouble(
            Dictionary<string, object> payload,
            string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            try
            {
                if (value is double)
                {
                    return (double)value;
                }

                if (value is decimal)
                {
                    return (double)(decimal)value;
                }

                if (value is int)
                {
                    return (int)value;
                }

                if (value is long)
                {
                    return (long)value;
                }

                string text = value as string;
                double parsed;
                if (text != null
                    && double.TryParse(
                        text,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out parsed))
                {
                    return parsed;
                }
            }
            catch (OverflowException)
            {
            }

            return null;
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
            internal string Model;
            internal double? ContextUsedPercent;
            internal bool ContextFromStatusLine;
            internal string TranscriptPath;
            internal DateTime LastTranscriptReadUtc;
        }
    }
}
