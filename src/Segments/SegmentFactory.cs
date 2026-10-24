using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VibeBar
{
    /// <summary>
    /// 快照 → UI 视图模型。Provider 不直接接触 UI；数值统一固定两位小数，
    /// 避免刷新时文本抖动。未知余额一律显示「—」，绝不显示 0。
    /// </summary>
    internal static class SegmentFactory
    {
        internal const string DefaultPeakRuleSummary =
            "峰时：工作日 09:00–12:00 / 14:00–18:00，周末全天谷时";

        internal const string SettingsPeakRuleSummary =
            "工作日 09:00–12:00 / 14:00–18:00，周末全天谷时";

        private static decimal _warnBelow = 20m;
        private static decimal _criticalBelow = 5m;
        private static string _peakRuleSummary = DefaultPeakRuleSummary;

        internal static void Configure(decimal warnBelow, decimal criticalBelow)
        {
            _warnBelow = warnBelow;
            _criticalBelow = criticalBelow;
        }

        internal static void ConfigurePeakRules(PeakValleySettings settings)
        {
            _peakRuleSummary = BuildPeakRuleSummary(settings);
        }

        /// <summary>
        /// 根据当前峰谷配置生成面板摘要；非法/空配置回退默认文案。
        /// </summary>
        internal static string BuildPeakRuleSummary(PeakValleySettings settings)
        {
            if (settings == null
                || settings.PeakWindows == null
                || settings.PeakWindows.Length == 0)
            {
                return DefaultPeakRuleSummary;
            }

            StringBuilder windows = new StringBuilder();
            for (int index = 0; index < settings.PeakWindows.Length; index++)
            {
                PeakWindow window = settings.PeakWindows[index];
                TimeSpan start;
                TimeSpan end;
                if (window == null
                    || !PeakValley.TryParseTimeOfDay(window.Start, out start)
                    || !PeakValley.TryParseTimeOfDay(window.End, out end))
                {
                    return DefaultPeakRuleSummary;
                }

                if (windows.Length > 0)
                {
                    windows.Append(" / ");
                }

                windows.Append(FormatRuleTime(start));
                windows.Append("–");
                windows.Append(FormatRuleTime(end));
            }

            if (settings.WeekendOffPeak)
            {
                return "峰时：工作日 " + windows + "，周末全天谷时";
            }

            return "峰时：每天 " + windows;
        }

        private static string FormatRuleTime(TimeSpan value)
        {
            int hours = (int)value.TotalHours;
            return hours.ToString("00", CultureInfo.InvariantCulture)
                + ":"
                + value.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        internal static SegmentVisual[] CreatePlaceholders()
        {
            return new SegmentVisual[]
            {
                CreateQuota(null, false),
                CreateGitPlaceholder(),
                CreateAgentPlaceholder()
            };
        }

        internal static SegmentVisual CreateGitPlaceholder()
        {
            return Create(SegmentKind.Git, "Git —", "Git", 20);
        }

        internal static SegmentVisual CreateAgentPlaceholder()
        {
            return Create(SegmentKind.Agent, "会话 —", "会话", 30);
        }

        internal static SegmentVisual CreateGit(GitSnapshot snapshot, bool privacy)
        {
            SegmentVisual visual = new SegmentVisual();
            visual.Kind = SegmentKind.Git;
            visual.Glyph = string.Empty;
            visual.Pulse = false;
            visual.Priority = 20;

            if (snapshot == null
                || snapshot.Health == SnapshotHealth.Unavailable
                || snapshot.Health == SnapshotHealth.Error)
            {
                visual.Text = "Git —";
                visual.Role = snapshot != null
                    && snapshot.Health == SnapshotHealth.Error
                        ? ColorRole.Warning
                        : ColorRole.Neutral;
                visual.Tooltip = snapshot == null
                    || string.IsNullOrEmpty(snapshot.Error)
                        ? "Git 状态"
                        : "Git：" + snapshot.Error;
                return visual;
            }

            string branch = snapshot.IsDetached
                ? "detached"
                : (string.IsNullOrEmpty(snapshot.Branch) ? "—" : snapshot.Branch);
            if (privacy)
            {
                branch = "…";
            }

            string text = branch;
            int dirty = snapshot.DirtyCount;
            if (dirty > 0)
            {
                text = text + "*" + dirty.ToString(CultureInfo.InvariantCulture);
            }

            if (snapshot.ConflictCount > 0)
            {
                text = text
                    + "!"
                    + snapshot.ConflictCount.ToString(CultureInfo.InvariantCulture);
            }

            if (snapshot.Ahead != 0 || snapshot.Behind != 0)
            {
                text = text
                    + "↑"
                    + snapshot.Ahead.ToString(CultureInfo.InvariantCulture)
                    + "↓"
                    + snapshot.Behind.ToString(CultureInfo.InvariantCulture);
            }

            visual.Text = text;
            visual.Role = snapshot.ConflictCount > 0
                ? ColorRole.Critical
                : (snapshot.Health == SnapshotHealth.Stale
                    ? ColorRole.Warning
                    : ColorRole.Neutral);
            visual.Tooltip = "Git 状态";
            return visual;
        }

        internal static DetailSection CreateGitDetail(
            GitSnapshot snapshot,
            bool privacy)
        {
            DetailSection section = new DetailSection();
            section.Title = "Git";
            section.Actions = new DetailAction[0];

            List<string> lines = new List<string>();

            if (snapshot == null)
            {
                lines.Add("未选择仓库");
                lines.Add("请在设置中选择 Git 仓库路径");
                section.Lines = lines.ToArray();
                return section;
            }

            bool noRepo = snapshot.Health == SnapshotHealth.Unavailable
                && (string.IsNullOrEmpty(snapshot.RepoPath)
                    || string.Equals(
                        snapshot.Error,
                        "未选择仓库",
                        StringComparison.Ordinal));

            if (noRepo)
            {
                lines.Add("未选择仓库");
                lines.Add("请在设置中选择 Git 仓库路径");
                section.Lines = lines.ToArray();
                return section;
            }

            string repoName = string.IsNullOrEmpty(snapshot.RepoName)
                ? snapshot.RepoPath
                : snapshot.RepoName;
            if (string.IsNullOrEmpty(repoName))
            {
                repoName = "—";
            }

            lines.Add("仓库：" + (privacy ? "…" : repoName));

            if (snapshot.Health == SnapshotHealth.Unavailable
                || snapshot.Health == SnapshotHealth.Error)
            {
                if (!string.IsNullOrEmpty(snapshot.Error))
                {
                    lines.Add("原因：" + snapshot.Error);
                }

                if (string.Equals(
                    snapshot.Error,
                    "仓库路径不存在",
                    StringComparison.Ordinal))
                {
                    lines.Add("请在设置中重新选择仓库路径");
                }
                else if (string.Equals(
                    snapshot.Error,
                    "不是 Git 仓库",
                    StringComparison.Ordinal))
                {
                    lines.Add("请在设置中选择 Git 仓库根目录");
                }
                else if (string.Equals(
                    snapshot.Error,
                    "未找到 git",
                    StringComparison.Ordinal))
                {
                    lines.Add("请安装 Git 并确保 git.exe 在 PATH 中");
                }

                if (snapshot.UpdatedUtc != DateTime.MinValue)
                {
                    lines.Add(
                        "最后更新："
                        + snapshot.UpdatedUtc.ToLocalTime().ToString(
                            "yyyy-MM-dd HH:mm:ss",
                            CultureInfo.InvariantCulture));
                }

                section.Lines = lines.ToArray();
                return section;
            }

            string branch = snapshot.IsDetached
                ? "detached（分离 HEAD）"
                : (string.IsNullOrEmpty(snapshot.Branch) ? "—" : snapshot.Branch);
            lines.Add("分支：" + (privacy ? "…" : branch));
            lines.Add(
                "已暂存："
                + snapshot.StagedCount.ToString(CultureInfo.InvariantCulture));
            lines.Add(
                "已修改："
                + snapshot.ModifiedCount.ToString(CultureInfo.InvariantCulture));
            lines.Add(
                "未跟踪："
                + snapshot.UntrackedCount.ToString(CultureInfo.InvariantCulture));
            lines.Add(
                "领先："
                + snapshot.Ahead.ToString(CultureInfo.InvariantCulture)
                + " · 落后："
                + snapshot.Behind.ToString(CultureInfo.InvariantCulture));

            if (snapshot.ConflictCount > 0)
            {
                lines.Add(
                    "冲突："
                    + snapshot.ConflictCount.ToString(CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(snapshot.Operation))
            {
                lines.Add("操作中：" + snapshot.Operation);
            }

            if (snapshot.UpdatedUtc != DateTime.MinValue)
            {
                lines.Add(
                    "最后更新："
                    + snapshot.UpdatedUtc.ToLocalTime().ToString(
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture));
            }

            if (snapshot.Health == SnapshotHealth.Stale)
            {
                lines.Add("数据可能过期");
            }

            if (!string.IsNullOrEmpty(snapshot.Error))
            {
                lines.Add("错误：" + snapshot.Error);
            }

            section.Lines = lines.ToArray();
            return section;
        }

        internal static SegmentVisual CreateAgent(
            AgentSnapshot snapshot,
            AgentSnapshot[] sessions,
            bool privacy)
        {
            SegmentVisual visual = new SegmentVisual();
            visual.Kind = SegmentKind.Agent;
            visual.Glyph = string.Empty;
            visual.Priority = 30;

            int sessionCount;
            if (sessions != null)
            {
                sessionCount = sessions.Length;
            }
            else
            {
                sessionCount = snapshot == null ? 0 : 1;
            }

            if (sessionCount <= 0 || (snapshot == null && sessions == null))
            {
                visual.Text = "会话 —";
                visual.Role = ColorRole.Neutral;
                visual.Pulse = false;
                visual.Tooltip = "Claude 会话";
                return visual;
            }

            bool anyWaitingPermission = false;
            if (sessions != null)
            {
                for (int index = 0; index < sessions.Length; index++)
                {
                    if (sessions[index] != null
                        && sessions[index].State == AgentState.WaitingPermission)
                    {
                        anyWaitingPermission = true;
                        break;
                    }
                }
            }
            else if (snapshot != null)
            {
                anyWaitingPermission = snapshot.State == AgentState.WaitingPermission;
            }

            if (sessionCount > 1)
            {
                visual.Text = sessionCount.ToString(CultureInfo.InvariantCulture)
                    + " 会话"
                    + (anyWaitingPermission ? " · 等待确认" : string.Empty);
                visual.Role = anyWaitingPermission
                    ? ColorRole.Waiting
                    : ColorRole.Neutral;
                visual.Pulse = anyWaitingPermission;
                visual.Tooltip = "Claude 会话："
                    + sessionCount.ToString(CultureInfo.InvariantCulture)
                    + " 个";
                return visual;
            }

            AgentSnapshot current = sessions != null && sessions.Length > 0
                ? sessions[0]
                : snapshot;
            if (current == null)
            {
                visual.Text = "会话 —";
                visual.Role = ColorRole.Neutral;
                visual.Pulse = false;
                visual.Tooltip = "Claude 会话";
                return visual;
            }

            visual.Text = AgentStateText(current.State, current.CurrentTool);
            if (current.ContextUsedPercent.HasValue
                && (current.State == AgentState.Idle
                    || current.State == AgentState.Done))
            {
                visual.Text = visual.Text
                    + " "
                    + Math.Round(current.ContextUsedPercent.Value)
                        .ToString("0", CultureInfo.InvariantCulture)
                    + "%";
            }

            visual.Role = AgentStateRole(current.State);
            visual.Pulse = current.State == AgentState.WaitingPermission;

            string tooltip = "Claude 会话";
            if (!privacy && !string.IsNullOrEmpty(current.ProjectName))
            {
                tooltip = tooltip + " · " + current.ProjectName;
            }

            if (current.ContextUsedPercent.HasValue)
            {
                tooltip = tooltip
                    + " · 上下文 "
                    + Math.Round(current.ContextUsedPercent.Value)
                        .ToString("0", CultureInfo.InvariantCulture)
                    + "%";
            }

            if (current.IsStale)
            {
                tooltip = tooltip + " · 数据可能过期";
            }

            visual.Tooltip = tooltip;
            return visual;
        }

        internal static DetailSection CreateAgentDetail(
            AgentSnapshot[] sessions,
            bool privacy)
        {
            DetailSection section = new DetailSection();
            section.Title = "Claude 会话";
            section.Actions = new DetailAction[0];

            List<string> lines = new List<string>();
            int written = 0;

            if (sessions != null)
            {
                for (int index = 0; index < sessions.Length; index++)
                {
                    AgentSnapshot session = sessions[index];
                    if (session == null)
                    {
                        continue;
                    }

                    string projectName = string.IsNullOrEmpty(session.ProjectName)
                        ? "未知项目"
                        : session.ProjectName;
                    if (privacy)
                    {
                        projectName = "…";
                    }

                    lines.Add(
                        projectName
                        + " · "
                        + AgentStateText(session.State, session.CurrentTool));

                    string modelLine = string.Empty;
                    if (!string.IsNullOrEmpty(session.Model))
                    {
                        modelLine = "模型：" + session.Model;
                    }

                    if (session.ContextUsedPercent.HasValue)
                    {
                        string percent = Math.Round(session.ContextUsedPercent.Value)
                            .ToString("0", CultureInfo.InvariantCulture)
                            + "%";
                        modelLine = string.IsNullOrEmpty(modelLine)
                            ? "上下文：" + percent
                            : modelLine + " · 上下文 " + percent;
                    }

                    if (!string.IsNullOrEmpty(modelLine))
                    {
                        lines.Add(modelLine);
                    }

                    string lastEvent = session.LastEventUtc == default(DateTime)
                        ? "—"
                        : session.LastEventUtc.ToLocalTime().ToString(
                            "yyyy-MM-dd HH:mm:ss",
                            CultureInfo.InvariantCulture);
                    string eventLine = "最后事件：" + lastEvent;
                    if (session.IsStale)
                    {
                        eventLine = eventLine + " · 数据可能过期";
                    }

                    lines.Add(eventLine);
                    written++;
                }
            }

            if (written == 0)
            {
                lines.Add("暂无活动会话");
            }

            section.Lines = lines.ToArray();
            return section;
        }

        internal static string AgentStateText(AgentState state, string currentTool)
        {
            if (state == AgentState.WaitingPermission)
            {
                return "等待确认";
            }

            if (state == AgentState.WaitingQuota)
            {
                return "额度等待";
            }

            if (state == AgentState.Thinking)
            {
                return "思考中";
            }

            if (state == AgentState.Tool)
            {
                return string.IsNullOrEmpty(currentTool) ? "工具" : currentTool;
            }

            if (state == AgentState.Done)
            {
                return "完成";
            }

            if (state == AgentState.Idle)
            {
                return "空闲";
            }

            if (state == AgentState.Error)
            {
                return "错误";
            }

            return "未知";
        }

        private static ColorRole AgentStateRole(AgentState state)
        {
            if (state == AgentState.WaitingPermission)
            {
                return ColorRole.Waiting;
            }

            if (state == AgentState.WaitingQuota)
            {
                return ColorRole.Warning;
            }

            if (state == AgentState.Thinking || state == AgentState.Tool)
            {
                return ColorRole.Accent;
            }

            if (state == AgentState.Done)
            {
                return ColorRole.Success;
            }

            if (state == AgentState.Error)
            {
                return ColorRole.Critical;
            }

            if (state == AgentState.Unknown)
            {
                return ColorRole.Warning;
            }

            return ColorRole.Neutral;
        }

        internal static SegmentVisual CreateQuota(QuotaSnapshot snapshot, bool privacy)
        {
            SegmentVisual visual = new SegmentVisual();
            visual.Kind = SegmentKind.Quota;
            visual.Glyph = string.Empty;
            visual.Tooltip = "DeepSeek 额度";
            visual.Pulse = false;
            visual.Priority = 10;

            if (snapshot == null || !snapshot.Balance.HasValue)
            {
                visual.Text = "额度 —";
                visual.Role = ColorRole.Neutral;
                return visual;
            }

            string amount = FormatAmount(snapshot.Balance, snapshot.Currency, privacy);
            visual.Text = amount + (snapshot.IsOffPeak ? " 谷" : " 峰");
            visual.Role = ResolveRole(snapshot);
            return visual;
        }

        internal static DetailSection CreateQuotaDetail(
            QuotaSnapshot snapshot,
            bool privacy)
        {
            DetailSection section = new DetailSection();
            section.Title = "DeepSeek";
            section.Actions = new DetailAction[0];

            List<string> lines = new List<string>();

            if (snapshot == null)
            {
                lines.Add("余额：—");
                lines.Add("未配置 API Key");
                lines.Add(_peakRuleSummary);
                lines.Add("数据来源：DeepSeek 官方接口");
                section.Lines = lines.ToArray();
                return section;
            }

            lines.Add("余额：" + FormatAmount(snapshot.Balance, snapshot.Currency, privacy));

            if (snapshot.GrantedBalance.HasValue)
            {
                lines.Add(
                    "赠送余额："
                    + FormatAmount(snapshot.GrantedBalance, snapshot.Currency, privacy));
            }

            if (snapshot.ToppedUpBalance.HasValue)
            {
                lines.Add(
                    "充值余额："
                    + FormatAmount(snapshot.ToppedUpBalance, snapshot.Currency, privacy));
            }

            lines.Add(BuildPeriodLine(snapshot));
            lines.Add(_peakRuleSummary);

            if (snapshot.UpdatedUtc != DateTime.MinValue)
            {
                lines.Add(
                    "最后更新："
                    + snapshot.UpdatedUtc.ToLocalTime().ToString(
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture));
            }

            if (snapshot.Health == SnapshotHealth.Stale)
            {
                lines.Add(
                    string.IsNullOrEmpty(snapshot.Error)
                        ? "数据可能过期"
                        : "数据可能过期：" + snapshot.Error);
            }
            else if (snapshot.Health == SnapshotHealth.Error)
            {
                lines.Add(
                    string.IsNullOrEmpty(snapshot.Error)
                        ? "错误：未知错误"
                        : "错误：" + snapshot.Error);
            }
            else if (snapshot.Health == SnapshotHealth.Unavailable)
            {
                lines.Add(
                    string.IsNullOrEmpty(snapshot.Error)
                        ? "未配置 API Key"
                        : snapshot.Error);
            }

            lines.Add("数据来源：" + SourceText(snapshot.Source));

            section.Lines = lines.ToArray();
            return section;
        }

        internal static string FormatCountdown(DateTime utcNow, DateTime targetUtc)
        {
            if (targetUtc == default(DateTime)
                || targetUtc.Year >= 9000)
            {
                return null;
            }

            if (utcNow.Kind != DateTimeKind.Utc)
            {
                utcNow = utcNow.ToUniversalTime();
            }

            if (targetUtc.Kind != DateTimeKind.Utc)
            {
                targetUtc = DateTime.SpecifyKind(targetUtc, DateTimeKind.Utc);
            }

            TimeSpan remaining = targetUtc - utcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return "0m";
            }

            long totalMinutes = (long)Math.Floor(remaining.TotalMinutes);
            long hours = totalMinutes / 60;
            long minutes = totalMinutes % 60;

            if (hours > 0)
            {
                return hours.ToString(CultureInfo.InvariantCulture)
                    + "h"
                    + minutes.ToString("00", CultureInfo.InvariantCulture)
                    + "m";
            }

            return minutes.ToString(CultureInfo.InvariantCulture) + "m";
        }

        private static string BuildPeriodLine(QuotaSnapshot snapshot)
        {
            string state = snapshot.IsOffPeak ? "谷" : "峰";
            string target = snapshot.IsOffPeak ? "峰" : "谷";
            string prefix = "当前" + state + "时 · 距" + target + "时";

            string countdown = FormatCountdown(
                DateTime.UtcNow,
                snapshot.NextTransitionUtc);
            if (string.IsNullOrEmpty(countdown))
            {
                return prefix;
            }

            return prefix + " " + countdown;
        }

        private static string FormatAmount(
            decimal? value,
            string currency,
            bool privacy)
        {
            if (!value.HasValue)
            {
                return "—";
            }

            string symbol = CurrencySymbol(currency);
            if (privacy)
            {
                return symbol + "•••";
            }

            return symbol
                + value.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string CurrencySymbol(string currency)
        {
            if (string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase))
            {
                return "¥";
            }

            if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
            {
                return "$";
            }

            if (string.IsNullOrEmpty(currency))
            {
                return string.Empty;
            }

            return currency + " ";
        }

        private static ColorRole ResolveRole(QuotaSnapshot snapshot)
        {
            if (snapshot.Balance.HasValue)
            {
                if (snapshot.Balance.Value < _criticalBelow)
                {
                    return ColorRole.Critical;
                }

                if (snapshot.Balance.Value < _warnBelow)
                {
                    return ColorRole.Warning;
                }
            }

            if (snapshot.Health == SnapshotHealth.Stale
                || snapshot.Health == SnapshotHealth.Error)
            {
                return ColorRole.Warning;
            }

            return ColorRole.Neutral;
        }

        private static string SourceText(SnapshotSource source)
        {
            if (source == SnapshotSource.Official)
            {
                return "DeepSeek 官方接口";
            }

            if (source == SnapshotSource.LocalLog)
            {
                return "本地日志";
            }

            if (source == SnapshotSource.Experimental)
            {
                return "实验性来源";
            }

            return "未知";
        }

        private static SegmentVisual Create(
            SegmentKind kind,
            string text,
            string tooltip,
            int priority)
        {
            SegmentVisual visual = new SegmentVisual();
            visual.Kind = kind;
            visual.Glyph = string.Empty;
            visual.Text = text;
            visual.Tooltip = tooltip;
            visual.Role = ColorRole.Neutral;
            visual.Pulse = false;
            visual.Priority = priority;
            return visual;
        }
    }
}
