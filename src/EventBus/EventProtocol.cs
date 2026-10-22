using System;
using System.Globalization;
using System.Text;

namespace VibeBar
{
    /// <summary>
    /// 主程序与 VibeBar.Cli.exe 共享的 IPC 协议常量与信封生成工具。
    /// 本文件只依赖 System，不得引用 System.Windows.*；payload 原样透传，
    /// 不在此处解析。
    /// </summary>
    internal static class EventProtocol
    {
        internal const string PipeName = "VibeBar.Events.v1";
        internal const int ProtocolVersion = 1;
        internal const int MaxMessageBytes = 64 * 1024;
        internal const string HookKind = "hook";
        internal const string StatusLineKind = "statusline";

        /// <summary>
        /// 生成单行信封：{"v":1,"kind":"...","ts":"...","payload":...}。
        /// 只做字符串拼接，payloadJson 必须是调用方提供的 JSON 文本。
        /// </summary>
        internal static string CreateEnvelope(string kind, string payloadJson)
        {
            string safeKind = kind == null ? string.Empty : kind;
            string safePayload = payloadJson == null ? "{}" : payloadJson;
            string timestamp = DateTime.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture);

            StringBuilder builder = new StringBuilder(safePayload.Length + 96);
            builder.Append("{\"v\":");
            builder.Append(ProtocolVersion.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"kind\":\"");
            builder.Append(EscapeJson(safeKind));
            builder.Append("\",\"ts\":\"");
            builder.Append(timestamp);
            builder.Append("\",\"payload\":");
            builder.Append(safePayload);
            builder.Append('}');
            return builder.ToString();
        }

        private static string EscapeJson(string value)
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
    }
}
