using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeBar
{
    /// <summary>
    /// 设置窗口的纯逻辑校验：关注端口文本解析/格式化、自定义命令列表合法性。
    /// 只依赖 System（不引用 System.Windows.*），因此可以被 tests\SelfTest.exe 直接编译；
    /// 所有失败文案都是可直接展示给用户的中文。
    /// </summary>
    internal static class SettingsValidation
    {
        internal const int MaxPortCount = 20;
        internal const int MinPort = 1;
        internal const int MaxPort = 65535;
        // 下限与 SettingsStore.NormalizeCustomCommands 保持一致：那边会把 <10 抬到 10，
        // 校验若放行 5-9 会变成「保存成功但实际生效 10 秒」的欺骗性成功。
        internal const int MinCommandIntervalSeconds = 10;
        internal const int MaxCommandIntervalSeconds = 3600;
        internal const int MaxCommandLabelLength = 12;

        // 命令 id：小写字母/数字开头，后接小写字母/数字/._-，总长 1-32。
        private const string CommandIdPattern = "^[a-z0-9][a-z0-9._-]{0,31}$";

        private static readonly char[] PortSeparators = new char[]
        {
            ',', ';', ' ', '\t', '\r', '\n'
        };

        /// <summary>
        /// 把「3000, 5173 8080」这类文本解析成端口数组。
        /// 分隔符为逗号/分号/空格/制表/换行；空串或全空白表示「显示全部监听端口」，
        /// 返回空数组且 error 为 null。重复端口去重并保留首次出现顺序。
        /// </summary>
        internal static bool TryParsePorts(string text, out int[] ports, out string error)
        {
            ports = new int[0];
            error = null;

            if (text == null || text.Trim().Length == 0)
            {
                return true;
            }

            string[] tokens = text.Split(PortSeparators, StringSplitOptions.RemoveEmptyEntries);
            List<int> result = new List<int>();
            for (int index = 0; index < tokens.Length; index++)
            {
                string token = tokens[index].Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                int port;
                if (!int.TryParse(
                        token,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out port))
                {
                    error = "端口 \"" + token + "\" 不是 1-65535 的整数";
                    ports = new int[0];
                    return false;
                }

                if (port < MinPort || port > MaxPort)
                {
                    error = "端口 \"" + token + "\" 不在 1-65535 范围内";
                    ports = new int[0];
                    return false;
                }

                if (!result.Contains(port))
                {
                    result.Add(port);
                }
            }

            if (result.Count > MaxPortCount)
            {
                error = "关注端口最多 " + MaxPortCount.ToString(CultureInfo.InvariantCulture) + " 个";
                ports = new int[0];
                return false;
            }

            ports = result.ToArray();
            return true;
        }

        /// <summary>
        /// 端口数组转成 TextBox 初值，例如 "3000, 5173"；空数组返回空串。
        /// </summary>
        internal static string FormatPorts(int[] ports)
        {
            if (ports == null || ports.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < ports.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(ports[index].ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>
        /// 校验自定义命令列表。通过时会顺手把 id 去空白、format 归一化为小写、
        /// label 截断到 12 字（这些是契约里的收敛行为，不报错）。
        /// </summary>
        internal static bool ValidateCommands(
            IList<CustomCommandSettings> commands,
            out string error)
        {
            error = null;

            if (commands == null)
            {
                return true;
            }

            Regex idPattern = new Regex(CommandIdPattern, RegexOptions.CultureInvariant);
            List<string> ids = new List<string>();
            for (int index = 0; index < commands.Count; index++)
            {
                CustomCommandSettings command = commands[index];
                string ordinal = (index + 1).ToString(CultureInfo.InvariantCulture);
                string id = command == null || command.Id == null
                    ? string.Empty
                    : command.Id.Trim();

                if (command == null || !idPattern.IsMatch(id))
                {
                    error = "第 " + ordinal + " 条命令的 ID \"" + id
                        + "\" 非法（小写字母/数字/._-，2-32 位）";
                    return false;
                }

                if (ids.Contains(id))
                {
                    error = "第 " + ordinal + " 条命令的 ID \"" + id + "\" 与前面的命令重复";
                    return false;
                }

                ids.Add(id);

                if (command.Command == null || command.Command.Trim().Length == 0)
                {
                    error = "第 " + ordinal + " 条命令的内容不能为空";
                    return false;
                }

                if (command.IntervalSeconds < MinCommandIntervalSeconds
                    || command.IntervalSeconds > MaxCommandIntervalSeconds)
                {
                    error = "第 " + ordinal + " 条命令的刷新间隔需在 "
                        + MinCommandIntervalSeconds.ToString(CultureInfo.InvariantCulture)
                        + "-"
                        + MaxCommandIntervalSeconds.ToString(CultureInfo.InvariantCulture)
                        + " 秒之间";
                    return false;
                }

                string format = command.Format == null ? string.Empty : command.Format.Trim();
                if (!string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    error = "第 " + ordinal + " 条命令的格式只能是 text 或 json";
                    return false;
                }

                command.Id = id;
                command.Format = string.Equals(
                    format,
                    "json",
                    StringComparison.OrdinalIgnoreCase)
                    ? "json"
                    : "text";
                command.Label = TruncateLabel(command.Label);
            }

            return true;
        }

        private static string TruncateLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return string.Empty;
            }

            string trimmed = label.Trim();
            if (trimmed.Length <= MaxCommandLabelLength)
            {
                return trimmed;
            }

            return trimmed.Substring(0, MaxCommandLabelLength);
        }
    }
}
