using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeBar
{
    /// <summary>
    /// 外部段协议 v1（见 docs/插件协议.md）。本文件只依赖 System 与 Models 中的
    /// 纯数据枚举，不得引用 System.Windows.*，也不得依赖 UI 层。
    /// 解析一行 JSON，失败时给出协议错误码，绝不抛异常。
    /// </summary>
    internal static class SegmentProtocol
    {
        internal const string PipeName = "VibeBar.Segment.v1";
        internal const int ProtocolVersion = 1;
        internal const int MaxFrameBytes = 4096;
        internal const int MaxTextLength = 24;
        internal const int MaxLabelLength = 8;
        internal const int MaxTooltipLength = 120;
        internal const int MaxUrlLength = 300;
        internal const int MaxDetailLines = 8;
        internal const int MaxDetailLineLength = 80;
        internal const int MinTtlSeconds = 5;
        internal const int MaxTtlSeconds = 3600;
        internal const int MaxFramesPerSecond = 20;
        internal const int DefaultPriority = 70;
        internal const int MinPriority = 0;
        internal const int MaxPriority = 1000;

        internal const string OpSet = "set";
        internal const string OpRemove = "remove";
        internal const string OpClear = "clear";

        internal const string ErrorInvalidJson = "invalid_json";
        internal const string ErrorUnsupportedVersion = "unsupported_version";
        internal const string ErrorInvalidOp = "invalid_op";
        internal const string ErrorInvalidId = "invalid_id";
        internal const string ErrorMissingText = "missing_text";
        internal const string ErrorTooManySegments = "too_many_segments";
        internal const string ErrorRateLimited = "rate_limited";

        internal const string OkReply = "ok";

        private static readonly Regex IdPattern = new Regex(
            "^[a-z0-9][a-z0-9._-]{0,39}$",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// 解析一行 JSON。失败返回 false，error 给出协议错误码
        /// （invalid_json / unsupported_version / invalid_op / invalid_id / missing_text）。
        /// </summary>
        internal static bool TryParse(
            string line,
            int defaultTtlSeconds,
            out ExternalSegmentFrame frame,
            out string error)
        {
            frame = null;
            error = null;

            if (line == null || line.Trim().Length == 0)
            {
                error = ErrorInvalidJson;
                return false;
            }

            Dictionary<string, object> payload;
            try
            {
                payload = Json.Deserialize<Dictionary<string, object>>(line);
            }
            catch (Exception)
            {
                error = ErrorInvalidJson;
                return false;
            }

            if (payload == null)
            {
                error = ErrorInvalidJson;
                return false;
            }

            int version;
            if (!TryGetInt(payload, "v", out version) || version != ProtocolVersion)
            {
                error = ErrorUnsupportedVersion;
                return false;
            }

            string op = NormalizeOp(GetText(payload, "op"));
            if (op == null)
            {
                error = ErrorInvalidOp;
                return false;
            }

            string id = GetText(payload, "id");
            if (id == null || !IsValidId(id))
            {
                error = ErrorInvalidId;
                return false;
            }

            ExternalSegmentFrame result = new ExternalSegmentFrame();
            result.Op = op;
            result.Id = id;
            result.Label = string.Empty;
            result.Text = string.Empty;
            result.Tooltip = string.Empty;
            result.Url = string.Empty;
            result.Role = ColorRole.Neutral;
            result.Priority = DefaultPriority;
            result.TtlSeconds = ClampTtl(defaultTtlSeconds);
            result.Detail = new string[0];

            if (string.Equals(op, OpSet, StringComparison.Ordinal))
            {
                object textValue;
                string text = null;
                if (payload.TryGetValue("text", out textValue))
                {
                    text = textValue as string;
                }

                // text 缺失（或不是字符串）是协议错误；空字符串是合法的「隐藏」语义。
                if (text == null)
                {
                    error = ErrorMissingText;
                    return false;
                }

                result.Text = Sanitize(text, MaxTextLength);
                result.Label = Sanitize(GetText(payload, "label"), MaxLabelLength);
                result.Tooltip = Sanitize(GetText(payload, "tooltip"), MaxTooltipLength);
                result.Url = Sanitize(GetText(payload, "url"), MaxUrlLength);
                result.Role = ParseRole(GetText(payload, "color"));

                int priority;
                if (TryGetInt(payload, "priority", out priority))
                {
                    result.Priority = ClampPriority(priority);
                }

                int ttl;
                if (TryGetInt(payload, "ttl", out ttl))
                {
                    result.TtlSeconds = ClampTtl(ttl);
                }

                result.Detail = ParseDetail(payload);
            }

            frame = result;
            return true;
        }

        /// <summary>
        /// clear 语义：命中 id 本身，以及所有以 "&lt;id&gt;." 开头的段；
        /// "demo" 命中 "demo.a"，不命中 "demox"。
        /// </summary>
        internal static bool MatchesClearPrefix(string segmentId, string clearId)
        {
            if (string.IsNullOrEmpty(segmentId) || string.IsNullOrEmpty(clearId))
            {
                return false;
            }

            if (string.Equals(segmentId, clearId, StringComparison.Ordinal))
            {
                return true;
            }

            return segmentId.StartsWith(clearId + ".", StringComparison.Ordinal);
        }

        internal static bool IsValidId(string id)
        {
            return !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);
        }

        internal static int ClampTtl(int seconds)
        {
            if (seconds < MinTtlSeconds)
            {
                return MinTtlSeconds;
            }

            if (seconds > MaxTtlSeconds)
            {
                return MaxTtlSeconds;
            }

            return seconds;
        }

        internal static int ClampPriority(int priority)
        {
            if (priority < MinPriority)
            {
                return MinPriority;
            }

            if (priority > MaxPriority)
            {
                return MaxPriority;
            }

            return priority;
        }

        internal static string BuildErrorReply(string error)
        {
            string code = string.IsNullOrEmpty(error) ? ErrorInvalidJson : error;
            return "{\"ok\":false,\"error\":\"" + code + "\"}";
        }

        internal static string BuildOkReply()
        {
            return "{\"ok\":true}";
        }

        private static string NormalizeOp(string op)
        {
            if (string.IsNullOrEmpty(op))
            {
                return null;
            }

            string value = op.Trim().ToLowerInvariant();
            if (string.Equals(value, OpSet, StringComparison.Ordinal)
                || string.Equals(value, OpRemove, StringComparison.Ordinal)
                || string.Equals(value, OpClear, StringComparison.Ordinal))
            {
                return value;
            }

            return null;
        }

        /// <summary>
        /// 文本净化：去控制字符、\r\n 折叠为一个空格、按上限截断并补 …。
        /// </summary>
        internal static string Sanitize(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            bool lastWasSpace = false;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current == '\r' || current == '\n')
                {
                    if (builder.Length > 0 && !lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;
                }

                if (current < ' ')
                {
                    continue;
                }

                builder.Append(current);
                lastWasSpace = current == ' ';
            }

            string text = builder.ToString().Trim();
            if (maxLength > 0 && text.Length > maxLength)
            {
                text = text.Substring(0, maxLength) + "…";
            }

            return text;
        }

        private static ColorRole ParseRole(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return ColorRole.Neutral;
            }

            string value = text.Trim();
            if (string.Equals(value, "accent", StringComparison.OrdinalIgnoreCase))
            {
                return ColorRole.Accent;
            }

            if (string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase))
            {
                return ColorRole.Warning;
            }

            if (string.Equals(value, "critical", StringComparison.OrdinalIgnoreCase))
            {
                return ColorRole.Critical;
            }

            if (string.Equals(value, "success", StringComparison.OrdinalIgnoreCase))
            {
                return ColorRole.Success;
            }

            if (string.Equals(value, "waiting", StringComparison.OrdinalIgnoreCase))
            {
                return ColorRole.Waiting;
            }

            return ColorRole.Neutral;
        }

        private static string[] ParseDetail(Dictionary<string, object> payload)
        {
            object value;
            if (!payload.TryGetValue("detail", out value) || value == null)
            {
                return new string[0];
            }

            object[] items = value as object[];
            if (items == null)
            {
                // JavaScriptSerializer 对 object 数组可能给 ArrayList。
                IList list = value as IList;
                if (list != null)
                {
                    items = new object[list.Count];
                    for (int index = 0; index < list.Count; index++)
                    {
                        items[index] = list[index];
                    }
                }
            }

            if (items == null)
            {
                return new string[0];
            }

            List<string> lines = new List<string>();
            for (int index = 0; index < items.Length; index++)
            {
                if (lines.Count >= MaxDetailLines)
                {
                    break;
                }

                string line = items[index] as string;
                if (line == null)
                {
                    continue;
                }

                string sanitized = Sanitize(line, MaxDetailLineLength);
                if (sanitized.Length == 0)
                {
                    continue;
                }

                lines.Add(sanitized);
            }

            return lines.ToArray();
        }

        private static string GetText(Dictionary<string, object> payload, string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            return value as string;
        }

        private static bool TryGetInt(
            Dictionary<string, object> payload,
            string key,
            out int result)
        {
            result = 0;
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return false;
            }

            try
            {
                if (value is int)
                {
                    result = (int)value;
                    return true;
                }

                if (value is long)
                {
                    result = checked((int)(long)value);
                    return true;
                }

                if (value is decimal)
                {
                    result = checked((int)(decimal)value);
                    return true;
                }

                if (value is double)
                {
                    result = checked((int)(double)value);
                    return true;
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            return false;
        }
    }

    /// <summary>
    /// 一帧外部段指令。字段已按协议净化/夹取，AppController 可直接落库。
    /// </summary>
    internal sealed class ExternalSegmentFrame
    {
        internal string Op;
        internal string Id;
        internal string Label;
        internal string Text;
        internal string Tooltip;
        internal ColorRole Role;
        internal string Url;
        internal int Priority;
        internal int TtlSeconds;
        internal string[] Detail;
    }
}
