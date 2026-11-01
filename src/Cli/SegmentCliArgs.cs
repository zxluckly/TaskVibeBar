using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VibeBar
{
    /// <summary>
    /// --segment / --segment-remove / --segment-clear 的参数解析与帧生成。
    /// 纯逻辑、不做 IO，可被 tests\SelfTest.exe 直接断言。
    /// 只依赖 System / System.Core（不得引入 System.Web.Extensions），
    /// 因此 JSON 由本类手写转义，帧格式必须与 SegmentProtocol.TryParse 对得上。
    /// </summary>
    internal sealed class SegmentCliArgs
    {
        internal const string SetArgument = "--segment";
        internal const string RemoveArgument = "--segment-remove";
        internal const string ClearArgument = "--segment-clear";
        internal const string VerboseArgument = "--verbose";

        internal const string OpSet = "set";
        internal const string OpRemove = "remove";
        internal const string OpClear = "clear";

        internal const string ErrorMissingId = "missing_id";
        internal const string ErrorMissingText = "missing_text";
        internal const string ErrorUnknownKeyPrefix = "unknown_key:";
        internal const string ErrorInvalidNumberPrefix = "invalid_number:";

        private const int MaxDetailLines = 8;

        internal string Op;
        internal string Id;
        internal string Label;
        internal string Text;
        internal string Tooltip;
        internal string Color;
        internal string Url;
        internal int Priority;
        internal int Ttl;
        internal string[] Detail;
        internal bool Verbose;
        internal string Error;

        private SegmentCliArgs()
        {
            Priority = -1;
            Ttl = -1;
        }

        /// <summary>
        /// args[0] 是 --segment / --segment-remove / --segment-clear（不区分大小写）时返回 true。
        /// </summary>
        internal static bool IsSegmentArgument(string[] args)
        {
            return args != null && args.Length > 0 && ResolveOp(args[0]) != null;
        }

        /// <summary>
        /// 解析参数，不抛异常。返回 false 表示这不是 segment 子命令；
        /// 返回 true 时 Error 非空代表参数错误（错误码见任务卡）。
        /// </summary>
        internal static bool TryParse(string[] args, out SegmentCliArgs result)
        {
            result = null;
            if (args == null || args.Length == 0)
            {
                return false;
            }

            string op = ResolveOp(args[0]);
            if (op == null)
            {
                return false;
            }

            SegmentCliArgs parsed = new SegmentCliArgs();
            parsed.Op = op;

            for (int index = 1; index < args.Length; index++)
            {
                string token = args[index];
                if (token == null)
                {
                    continue;
                }

                token = token.Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                if (string.Equals(token, VerboseArgument, StringComparison.OrdinalIgnoreCase))
                {
                    parsed.Verbose = true;
                    continue;
                }

                int separator = token.IndexOf('=');
                if (separator <= 0)
                {
                    parsed.Error = ErrorUnknownKeyPrefix + token;
                    result = parsed;
                    return true;
                }

                string key = token.Substring(0, separator).Trim().ToLowerInvariant();
                string value = token.Substring(separator + 1);
                if (!parsed.Apply(key, value))
                {
                    result = parsed;
                    return true;
                }
            }

            if (string.IsNullOrEmpty(parsed.Id))
            {
                parsed.Error = ErrorMissingId;
            }
            else if (string.Equals(parsed.Op, OpSet, StringComparison.Ordinal)
                && parsed.Text == null)
            {
                parsed.Error = ErrorMissingText;
            }

            result = parsed;
            return true;
        }

        /// <summary>
        /// 生成单行帧：{"v":1,"op":"set","id":...,"text":...}。
        /// 只输出调用方显式给出的字段（priority/ttl 用 -1 表示未指定）。
        /// </summary>
        internal string BuildFrameJson()
        {
            StringBuilder builder = new StringBuilder(256);
            builder.Append("{\"v\":1,\"op\":\"");
            builder.Append(EscapeJson(string.IsNullOrEmpty(Op) ? OpSet : Op));
            builder.Append("\",\"id\":\"");
            builder.Append(EscapeJson(Id));
            builder.Append('"');

            if (string.Equals(Op, OpSet, StringComparison.Ordinal))
            {
                // set 必须带 text：空字符串是合法的「隐藏」语义。
                builder.Append(",\"text\":\"");
                builder.Append(EscapeJson(Text == null ? string.Empty : Text));
                builder.Append('"');

                AppendOptionalString(builder, "label", Label);
                AppendOptionalString(builder, "tooltip", Tooltip);
                AppendOptionalString(builder, "color", Color);
                AppendOptionalString(builder, "url", Url);

                if (Priority >= 0)
                {
                    builder.Append(",\"priority\":");
                    builder.Append(Priority.ToString(CultureInfo.InvariantCulture));
                }

                if (Ttl >= 0)
                {
                    builder.Append(",\"ttl\":");
                    builder.Append(Ttl.ToString(CultureInfo.InvariantCulture));
                }

                if (Detail != null && Detail.Length > 0)
                {
                    builder.Append(",\"detail\":[");
                    for (int index = 0; index < Detail.Length; index++)
                    {
                        if (index > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append('"');
                        builder.Append(EscapeJson(Detail[index]));
                        builder.Append('"');
                    }

                    builder.Append(']');
                }
            }

            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// 手写 JSON 字符串转义：引号、反斜杠、\r\n\t 与控制字符。
        /// </summary>
        internal static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                if (current == '"')
                {
                    builder.Append("\\\"");
                }
                else if (current == '\\')
                {
                    builder.Append("\\\\");
                }
                else if (current == '\r')
                {
                    builder.Append("\\r");
                }
                else if (current == '\n')
                {
                    builder.Append("\\n");
                }
                else if (current == '\t')
                {
                    builder.Append("\\t");
                }
                else if (current < ' ')
                {
                    builder.Append("\\u");
                    builder.Append(((int)current).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(current);
                }
            }

            return builder.ToString();
        }

        private static string ResolveOp(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            string value = token.Trim();
            if (string.Equals(value, SetArgument, StringComparison.OrdinalIgnoreCase))
            {
                return OpSet;
            }

            if (string.Equals(value, RemoveArgument, StringComparison.OrdinalIgnoreCase))
            {
                return OpRemove;
            }

            if (string.Equals(value, ClearArgument, StringComparison.OrdinalIgnoreCase))
            {
                return OpClear;
            }

            return null;
        }

        private bool Apply(string key, string value)
        {
            if (string.Equals(key, "id", StringComparison.Ordinal))
            {
                Id = value;
                return true;
            }

            if (string.Equals(key, "text", StringComparison.Ordinal))
            {
                Text = value;
                return true;
            }

            if (string.Equals(key, "label", StringComparison.Ordinal))
            {
                Label = value;
                return true;
            }

            if (string.Equals(key, "tooltip", StringComparison.Ordinal))
            {
                Tooltip = value;
                return true;
            }

            if (string.Equals(key, "color", StringComparison.Ordinal))
            {
                Color = value;
                return true;
            }

            if (string.Equals(key, "url", StringComparison.Ordinal))
            {
                Url = value;
                return true;
            }

            if (string.Equals(key, "priority", StringComparison.Ordinal))
            {
                return TryReadNumber(key, value, out Priority);
            }

            if (string.Equals(key, "ttl", StringComparison.Ordinal))
            {
                return TryReadNumber(key, value, out Ttl);
            }

            if (string.Equals(key, "detail", StringComparison.Ordinal))
            {
                Detail = SplitDetail(value);
                return true;
            }

            Error = ErrorUnknownKeyPrefix + key;
            return false;
        }

        private bool TryReadNumber(string key, string value, out int number)
        {
            number = -1;
            string text = value == null ? string.Empty : value.Trim();
            if (text.Length == 0)
            {
                Error = ErrorInvalidNumberPrefix + key;
                return false;
            }

            int parsed;
            if (!int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed))
            {
                Error = ErrorInvalidNumberPrefix + key;
                return false;
            }

            number = parsed;
            return true;
        }

        /// <summary>
        /// detail=a|b|c 拆成多行；最多 8 行，超出的丢弃，空行丢弃。
        /// </summary>
        private static string[] SplitDetail(string value)
        {
            if (value == null)
            {
                return new string[0];
            }

            string[] parts = value.Split('|');
            List<string> lines = new List<string>();
            for (int index = 0; index < parts.Length; index++)
            {
                if (lines.Count >= MaxDetailLines)
                {
                    break;
                }

                string line = parts[index] == null ? string.Empty : parts[index].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                lines.Add(line);
            }

            return lines.ToArray();
        }

        private static void AppendOptionalString(
            StringBuilder builder,
            string name,
            string value)
        {
            if (value == null)
            {
                return;
            }

            builder.Append(",\"");
            builder.Append(name);
            builder.Append("\":\"");
            builder.Append(EscapeJson(value));
            builder.Append('"');
        }
    }
}
