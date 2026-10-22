using System;
using System.Collections.Generic;
using System.Globalization;

namespace VibeBar
{
    /// <summary>
    /// 峰谷判断（纯静态、可单测）。默认规则为北京时间工作日
    /// 09:00–12:00、14:00–18:00 峰时，周末全天谷时。
    /// 非法时间/时区回退默认规则并记日志，不崩溃。
    /// </summary>
    internal static class PeakValley
    {
        private const string DefaultTimeZoneId = "China Standard Time";

        private static readonly object LogSync = new object();
        private static readonly HashSet<string> LoggedWarnings = new HashSet<string>();
        private static readonly Dictionary<string, TimeZoneInfo> ZoneCache =
            new Dictionary<string, TimeZoneInfo>();
        private static readonly WindowRange[] DefaultWindows = CreateDefaultWindows();

        /// <summary>
        /// 最近一次 Evaluate 实际评估的候选边界数量（诊断/自测用）。
        /// 候选点算法每次只检查约 1–60 个边界，而不是逐分钟扫描 8 天。
        /// </summary>
        internal static int LastCandidateEvaluationCount;

        /// <summary>
        /// 判断指定 UTC 时刻是否谷时，并给出下一次峰/谷切换的 UTC 时刻。
        /// 最多向后查找 8 天；找不到时 nextTransitionUtc = DateTime.MaxValue(UTC)。
        /// </summary>
        internal static void Evaluate(
            DateTime utcNow,
            PeakValleySettings settings,
            out bool isOffPeak,
            out DateTime nextTransitionUtc)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
            {
                utcNow = utcNow.ToUniversalTime();
            }

            if (utcNow == DateTime.MinValue)
            {
                utcNow = DateTime.UtcNow;
            }

            TimeZoneInfo zone = ResolveTimeZone(settings);
            WindowRange[] windows = ResolveWindows(settings);
            bool weekendOffPeak = settings == null || settings.WeekendOffPeak;

            DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
            isOffPeak = EvaluateLocal(localNow, windows, weekendOffPeak);
            nextTransitionUtc = FindNextTransition(
                utcNow,
                localNow,
                zone,
                windows,
                weekendOffPeak,
                isOffPeak);
        }

        /// <summary>
        /// 候选点算法：只枚举未来 9 个本地日的午夜、每个峰窗的 start/end，
        /// 按 UTC 排序后找到第一个状态不同的边界。复杂度约 O(天 × 窗口数)，
        /// 不随 8 天逐分钟放大。
        /// </summary>
        private static DateTime FindNextTransition(
            DateTime utcNow,
            DateTime localNow,
            TimeZoneInfo zone,
            WindowRange[] windows,
            bool weekendOffPeak,
            bool isOffPeak)
        {
            List<DateTime> candidates = new List<DateTime>(64);
            DateTime firstDate = localNow.Date;

            for (int day = 0; day <= 9; day++)
            {
                DateTime date = firstDate.AddDays(day);
                AddCandidate(candidates, zone, date, TimeSpan.Zero);

                for (int index = 0; index < windows.Length; index++)
                {
                    AddCandidate(candidates, zone, date, windows[index].Start);
                    AddCandidate(candidates, zone, date, windows[index].End);
                }
            }

            candidates.Sort();

            DateTime limit = utcNow.AddDays(8);
            int evaluated = 0;

            for (int index = 0; index < candidates.Count; index++)
            {
                DateTime candidate = candidates[index];
                if (candidate <= utcNow)
                {
                    continue;
                }

                if (candidate > limit)
                {
                    break;
                }

                evaluated++;

                DateTime localCandidate;
                try
                {
                    localCandidate = TimeZoneInfo.ConvertTimeFromUtc(candidate, zone);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (EvaluateLocal(localCandidate, windows, weekendOffPeak) != isOffPeak)
                {
                    LastCandidateEvaluationCount = evaluated;
                    return candidate;
                }
            }

            LastCandidateEvaluationCount = evaluated;
            return DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        }

        private static void AddCandidate(
            List<DateTime> candidates,
            TimeZoneInfo zone,
            DateTime date,
            TimeSpan time)
        {
            DateTime local = date.Add(time);
            try
            {
                candidates.Add(TimeZoneInfo.ConvertTimeToUtc(local, zone));
            }
            catch (ArgumentException)
            {
                // DST 跳变导致的不存在本地时刻，跳过；China Standard Time 无 DST。
            }
        }

        private static bool EvaluateLocal(
            DateTime local,
            WindowRange[] windows,
            bool weekendOffPeak)
        {
            if (weekendOffPeak
                && (local.DayOfWeek == DayOfWeek.Saturday
                    || local.DayOfWeek == DayOfWeek.Sunday))
            {
                return true;
            }

            TimeSpan timeOfDay = local.TimeOfDay;
            for (int index = 0; index < windows.Length; index++)
            {
                WindowRange window = windows[index];
                if (window.Start <= window.End)
                {
                    if (timeOfDay >= window.Start && timeOfDay < window.End)
                    {
                        return false;
                    }
                }
                else
                {
                    // 跨午夜：22:00–02:00 视为 [22:00, 24:00) ∪ [00:00, 02:00)。
                    if (timeOfDay >= window.Start || timeOfDay < window.End)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static TimeZoneInfo ResolveTimeZone(PeakValleySettings settings)
        {
            string id = settings == null
                ? DefaultTimeZoneId
                : settings.TimeZone;

            if (string.IsNullOrEmpty(id))
            {
                LogOnce("peak.timezone.empty", "峰谷时区为空，已回退本地时区");
                return TimeZoneInfo.Local;
            }

            lock (ZoneCache)
            {
                TimeZoneInfo cached;
                if (ZoneCache.TryGetValue(id, out cached))
                {
                    return cached;
                }

                try
                {
                    TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(id);
                    ZoneCache[id] = zone;
                    return zone;
                }
                catch (Exception exception)
                {
                    LogOnce(
                        "peak.timezone.invalid",
                        "峰谷时区无效（"
                        + exception.GetType().Name
                        + "），已回退本地时区");
                    ZoneCache[id] = TimeZoneInfo.Local;
                    return TimeZoneInfo.Local;
                }
            }
        }

        private static WindowRange[] ResolveWindows(PeakValleySettings settings)
        {
            if (settings == null
                || settings.PeakWindows == null
                || settings.PeakWindows.Length == 0)
            {
                LogOnce("peak.windows.empty", "峰谷区间为空，已使用默认规则");
                return DefaultWindows;
            }

            WindowRange[] result = new WindowRange[settings.PeakWindows.Length];
            for (int index = 0; index < settings.PeakWindows.Length; index++)
            {
                PeakWindow window = settings.PeakWindows[index];
                TimeSpan start;
                TimeSpan end;
                if (window == null
                    || !TryParseTimeOfDay(window.Start, out start)
                    || !TryParseTimeOfDay(window.End, out end))
                {
                    LogOnce(
                        "peak.windows.invalid",
                        "峰谷区间时间格式非法，已使用默认规则");
                    return DefaultWindows;
                }

                WindowRange range = new WindowRange();
                range.Start = start;
                range.End = end;
                result[index] = range;
            }

            return result;
        }

        internal static bool TryParseTimeOfDay(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (!TimeSpan.TryParse(
                text.Trim(),
                CultureInfo.InvariantCulture,
                out value))
            {
                return false;
            }

            return value >= TimeSpan.Zero && value < TimeSpan.FromDays(1);
        }

        private static WindowRange[] CreateDefaultWindows()
        {
            WindowRange morning = new WindowRange();
            morning.Start = TimeSpan.FromHours(9);
            morning.End = TimeSpan.FromHours(12);

            WindowRange afternoon = new WindowRange();
            afternoon.Start = TimeSpan.FromHours(14);
            afternoon.End = TimeSpan.FromHours(18);

            return new WindowRange[] { morning, afternoon };
        }

        private static void LogOnce(string key, string message)
        {
            lock (LogSync)
            {
                if (LoggedWarnings.Contains(key))
                {
                    return;
                }

                LoggedWarnings.Add(key);
            }

            Log.Write(message);
        }

        private struct WindowRange
        {
            internal TimeSpan Start;
            internal TimeSpan End;
        }
    }
}
