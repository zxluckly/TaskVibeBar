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
