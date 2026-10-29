using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// 自定义命令段数据源（D4 / E1）。一个命令一个 Provider，Id 为 "cmd:&lt;id&gt;"。
    /// 命令由用户本人在 settings.json 中编写，超时固定 3 秒；
    /// 输出支持纯文本首行，或 JSON {"text","tooltip","color","url"}。
    /// </summary>
    internal sealed class CustomCommandProvider : IProvider
    {
        internal const string ProviderIdPrefix = "cmd:";
        internal const int CommandTimeoutMilliseconds = 3000;
        internal const int MaxTextLength = 48;
        private const int DefaultIntervalSeconds = 30;
        private const int MinIntervalSeconds = 10;
        private const int MaxIntervalSeconds = 3600;

        private readonly object _sync = new object();
        private readonly string _id;
        private readonly string _segmentId;

        private string _label;
        private string _command;
        private string _format;
        private string _url;
        private int _intervalSeconds;
        private bool _disposed;

        internal CustomCommandProvider(CustomCommandSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            _segmentId = settings.Id == null ? string.Empty : settings.Id.Trim();
            _id = ProviderIdPrefix + _segmentId;
            _label = settings.Label;
            _command = settings.Command;
            _format = settings.Format;
            _url = settings.Url;
            _intervalSeconds = NormalizeInterval(settings.IntervalSeconds);
        }

        public string Id
        {
            get { return _id; }
        }

        internal string SegmentId
        {
            get { return _segmentId; }
        }

        internal string Label
        {
            get
            {
                lock (_sync)
                {
                    return _label;
                }
            }
        }

        internal string Command
        {
            get
            {
                lock (_sync)
                {
                    return _command;
                }
            }
        }

        internal string Url
        {
            get
            {
                lock (_sync)
                {
                    return _url;
                }
            }
        }

        public TimeSpan PollInterval
        {
            get
            {
                lock (_sync)
                {
                    return TimeSpan.FromSeconds(_intervalSeconds);
                }
            }
        }

        public TimeSpan Timeout
        {
            get { return TimeSpan.FromMilliseconds(CommandTimeoutMilliseconds + 500); }
        }

        internal void UpdateSettings(CustomCommandSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            lock (_sync)
            {
                _label = settings.Label;
                _command = settings.Command;
                _format = settings.Format;
                _url = settings.Url;
                _intervalSeconds = NormalizeInterval(settings.IntervalSeconds);
            }
        }

        public async Task<ProviderResult> QueryAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            string command;
            string format;
            string label;
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException(token);
                }

                command = _command;
                format = _format;
                label = _label;
            }

            CustomCommandSnapshot snapshot = new CustomCommandSnapshot();
            snapshot.Id = _segmentId;
            snapshot.Label = label;
            snapshot.UpdatedUtc = DateTime.UtcNow;
            snapshot.Health = SnapshotHealth.Live;
            snapshot.Error = string.Empty;
            snapshot.Role = ColorRole.Neutral;

            if (string.IsNullOrEmpty(command) || command.Trim().Length == 0)
            {
                snapshot.Text = "—";
                snapshot.Health = SnapshotHealth.Unavailable;
                snapshot.Error = "命令为空";
                return CreateResult(snapshot);
            }

            Stopwatch watch = Stopwatch.StartNew();
            ProcessRunResult run = await ProcessRunner.RunAsync(
                "cmd.exe",
                "/d /c " + command,
                null,
                CommandTimeoutMilliseconds,
                ProcessRunner.ConsoleEncoding(),
                null,
                token).ConfigureAwait(false);
            watch.Stop();

            if (run.Cancelled)
            {
                throw new OperationCanceledException(token);
            }

            snapshot.DurationMilliseconds = (int)watch.ElapsedMilliseconds;

            if (run.NotFound)
            {
                snapshot.Health = SnapshotHealth.Error;
                snapshot.Error = "未找到 cmd.exe";
                return CreateResult(snapshot);
            }

            if (run.TimedOut)
            {
                snapshot.Health = SnapshotHealth.Error;
                snapshot.Error = "命令超时（3s）";
                return CreateResult(snapshot);
            }

            snapshot.ExitCode = run.ExitCode;
            ApplyOutput(snapshot, run.Stdout, format);

            if (run.ExitCode != 0 && snapshot.Role == ColorRole.Neutral)
            {
                snapshot.Role = ColorRole.Warning;
            }

            if (string.IsNullOrEmpty(snapshot.Text))
            {
                snapshot.Text = run.ExitCode == 0
                    ? "—"
                    : "退出码 " + run.ExitCode.ToString(CultureInfo.InvariantCulture);
            }

            return CreateResult(snapshot);
        }

        /// <summary>
        /// 按 format 解析输出：json 走 E1 协议，其余取首个非空行。
        /// 解析失败一律降级为纯文本，绝不让自定义命令影响主流程。
        /// </summary>
        internal static void ApplyOutput(
            CustomCommandSnapshot snapshot,
            string stdout,
            string format)
        {
            if (snapshot == null)
            {
                return;
            }

            string text = stdout == null ? string.Empty : stdout.Trim();
            if (text.Length == 0)
            {
                snapshot.Text = string.Empty;
                return;
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                if (TryApplyJson(snapshot, text))
                {
                    return;
                }
            }

            snapshot.Text = FirstLine(text, MaxTextLength);
        }

        private static bool TryApplyJson(CustomCommandSnapshot snapshot, string text)
        {
            Dictionary<string, object> payload;
            try
            {
                payload = Json.Deserialize<Dictionary<string, object>>(text);
            }
            catch (Exception exception)
            {
                Log.Debug("自定义命令 JSON 解析失败：" + exception.GetType().Name);
                return false;
            }

            if (payload == null)
            {
                return false;
            }

            object value;
            if (payload.TryGetValue("text", out value))
            {
                string parsed = value as string;
                if (parsed != null)
                {
                    snapshot.Text = FirstLine(parsed, MaxTextLength);
                }
            }

            if (payload.TryGetValue("tooltip", out value))
            {
                string tooltip = value as string;
                if (tooltip != null)
                {
                    snapshot.Tooltip = tooltip;
                }
            }

            if (payload.TryGetValue("color", out value))
            {
                string color = value as string;
                if (color != null)
                {
                    snapshot.Role = SegmentFactory.ParseColorRole(color);
                }
            }

            if (payload.TryGetValue("url", out value))
            {
                string url = value as string;
                if (url != null)
                {
                    snapshot.Url = url;
                }
            }

            if (string.IsNullOrEmpty(snapshot.Text))
            {
                snapshot.Text = FirstLine(text, MaxTextLength);
            }

            return true;
        }

        internal static string FirstLine(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            string result = string.Empty;
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Length > 0)
                {
                    result = line;
                    break;
                }
            }

            if (maxLength > 0 && result.Length > maxLength)
            {
                result = result.Substring(0, maxLength) + "…";
            }

            return result;
        }

        private static int NormalizeInterval(int seconds)
        {
            if (seconds <= 0)
            {
                return DefaultIntervalSeconds;
            }

            if (seconds < MinIntervalSeconds)
            {
                return MinIntervalSeconds;
            }

            if (seconds > MaxIntervalSeconds)
            {
                return MaxIntervalSeconds;
            }

            return seconds;
        }

        private static ProviderResult CreateResult(CustomCommandSnapshot snapshot)
        {
            ProviderResult result = new ProviderResult();
            result.ProviderId = ProviderIdPrefix + snapshot.Id;
            result.Snapshot = snapshot;
            result.Health = snapshot.Health;
            result.Error = snapshot.Error;
            result.UpdatedUtc = DateTime.UtcNow;
            return result;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
            }
        }
    }
}
