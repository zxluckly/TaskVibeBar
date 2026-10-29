using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// 当前仓库的 PR / CI 状态，数据来自 GitHub CLI：
    /// `gh pr view --json number,title,state,isDraft,reviewDecision,statusCheckRollup,url`。
    /// 没有打开的 PR、未安装 gh、未登录时都只影响该段，不影响其他 Provider。
    /// </summary>
    internal sealed class PrProvider : IProvider
    {
        internal const string ProviderIdValue = "pr";
        internal const int CommandTimeoutMilliseconds = 8000;
        private const int DefaultPollSeconds = 60;
        private const int MinPollSeconds = 30;
        private const int MaxPollSeconds = 3600;
        private const int MaxErrorLength = 160;

        private const string Fields =
            "number,title,state,isDraft,reviewDecision,statusCheckRollup,url,mergeable";

        private readonly object _sync = new object();
        private string _repoPath;
        private int _pollSeconds;
        private bool _disposed;

        internal PrProvider(string repoPath, int pollSeconds)
        {
            _repoPath = GitProvider.NormalizeRepoPath(repoPath);
            _pollSeconds = NormalizePollSeconds(pollSeconds);
        }

        public string Id
        {
            get { return ProviderIdValue; }
        }

        public TimeSpan PollInterval
        {
            get
            {
                lock (_sync)
                {
                    return TimeSpan.FromSeconds(_pollSeconds);
                }
            }
        }

        public TimeSpan Timeout
        {
            get { return TimeSpan.FromMilliseconds(CommandTimeoutMilliseconds + 1000); }
        }

        internal void UpdateSettings(string repoPath, int pollSeconds)
        {
            lock (_sync)
            {
                _repoPath = GitProvider.NormalizeRepoPath(repoPath);
                _pollSeconds = NormalizePollSeconds(pollSeconds);
            }
        }

        public async Task<ProviderResult> QueryAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            string repoPath;
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException(token);
                }

                repoPath = _repoPath;
            }

            if (string.IsNullOrEmpty(repoPath))
            {
                return CreateResult(CreateSnapshot(
                    false,
                    SnapshotHealth.Unavailable,
                    "未选择仓库"));
            }

            ProcessRunResult run = await ProcessRunner.RunAsync(
                "gh",
                "pr view --json " + Fields,
                repoPath,
                CommandTimeoutMilliseconds,
                new UTF8Encoding(false),
                null,
                token).ConfigureAwait(false);

            if (run.Cancelled)
            {
                throw new OperationCanceledException(token);
            }

            if (run.NotFound)
            {
                Log.Debug("PR 查询跳过：未找到 gh");
                return CreateResult(CreateSnapshot(
                    false,
                    SnapshotHealth.Unavailable,
                    "未找到 gh（GitHub CLI）"));
            }

            if (run.TimedOut)
            {
                return CreateResult(CreateSnapshot(
                    false,
                    SnapshotHealth.Error,
                    "gh 命令超时"));
            }

            if (run.ExitCode != 0)
            {
                string stderr = GitProvider.FirstLine(run.Stderr, MaxErrorLength);
                if (Contains(stderr, "no pull requests found")
                    || Contains(stderr, "no open pull requests"))
                {
                    return CreateResult(CreateSnapshot(false, SnapshotHealth.Live, string.Empty));
                }

                if (Contains(stderr, "not a git repository"))
                {
                    return CreateResult(CreateSnapshot(
                        false,
                        SnapshotHealth.Unavailable,
                        "不是 Git 仓库"));
                }

                if (Contains(stderr, "auth")
                    || Contains(stderr, "login")
                    || Contains(stderr, "authentication"))
                {
                    return CreateResult(CreateSnapshot(
                        false,
                        SnapshotHealth.Error,
                        "gh 未登录，请执行 gh auth login"));
                }

                string error = string.IsNullOrEmpty(stderr)
                    ? "gh 命令失败（退出码 "
                        + run.ExitCode.ToString(CultureInfo.InvariantCulture)
                        + "）"
                    : "gh 命令失败：" + stderr;
                Log.Write("PR 查询失败：" + error);
                return CreateResult(CreateSnapshot(false, SnapshotHealth.Error, error));
            }

            PrSnapshot snapshot;
            try
            {
                snapshot = ParsePr(run.Stdout);
            }
            catch (Exception exception)
            {
                Log.Write("PR 输出解析失败：" + exception.GetType().Name);
                return CreateResult(CreateSnapshot(
                    false,
                    SnapshotHealth.Error,
                    "gh 输出解析失败"));
            }

            return CreateResult(snapshot);
        }

        /// <summary>
        /// 解析 `gh pr view --json` 的输出；供自测直接调用。
        /// </summary>
        internal static PrSnapshot ParsePr(string json)
        {
            PrSnapshot snapshot = new PrSnapshot();
            snapshot.UpdatedUtc = DateTime.UtcNow;
            snapshot.Health = SnapshotHealth.Live;
            snapshot.Error = string.Empty;

            if (string.IsNullOrEmpty(json) || json.Trim().Length == 0)
            {
                snapshot.Exists = false;
                return snapshot;
            }

            Dictionary<string, object> root =
                Json.Deserialize<Dictionary<string, object>>(json);
            if (root == null)
            {
                snapshot.Exists = false;
                return snapshot;
            }

            snapshot.Exists = true;
            snapshot.Number = ReadInt(root, "number");
            snapshot.Title = ReadString(root, "title");
            snapshot.State = ReadString(root, "state");
            snapshot.Url = ReadString(root, "url");
            snapshot.ReviewDecision = ReadString(root, "reviewDecision");
            snapshot.IsDraft = ReadBool(root, "isDraft");

            object value;
            if (root.TryGetValue("statusCheckRollup", out value))
            {
                System.Collections.IList checks = value as System.Collections.IList;
                if (checks != null)
                {
                    for (int index = 0; index < checks.Count; index++)
                    {
                        Dictionary<string, object> check =
                            checks[index] as Dictionary<string, object>;
                        if (check == null)
                        {
                            continue;
                        }

                        string status = ReadString(check, "conclusion");
                        if (string.IsNullOrEmpty(status))
                        {
                            status = ReadString(check, "state");
                        }

                        if (IsFailedStatus(status))
                        {
                            snapshot.ChecksFailed++;
                        }
                        else if (IsPassedStatus(status))
                        {
                            snapshot.ChecksPassed++;
                        }
                        else
                        {
                            snapshot.ChecksPending++;
                        }
                    }
                }
            }

            return snapshot;
        }

        private static bool IsFailedStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
            {
                return false;
            }

            return string.Equals(status, "FAILURE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "TIMED_OUT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "ACTION_REQUIRED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "STARTUP_FAILURE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPassedStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
            {
                return false;
            }

            return string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "NEUTRAL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "SKIPPED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "PASSING", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadString(Dictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return string.Empty;
            }

            string text = value as string;
            return text == null ? string.Empty : text;
        }

        private static int ReadInt(Dictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return 0;
            }

            try
            {
                if (value is int)
                {
                    return (int)value;
                }

                if (value is long)
                {
                    return checked((int)(long)value);
                }

                if (value is decimal)
                {
                    return (int)(decimal)value;
                }

                if (value is double)
                {
                    return (int)(double)value;
                }
            }
            catch (OverflowException)
            {
            }

            return 0;
        }

        private static bool ReadBool(Dictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value) || value == null)
            {
                return false;
            }

            return value is bool && (bool)value;
        }

        private static bool Contains(string text, string keyword)
        {
            return !string.IsNullOrEmpty(text)
                && text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static PrSnapshot CreateSnapshot(
            bool exists,
            SnapshotHealth health,
            string error)
        {
            PrSnapshot snapshot = new PrSnapshot();
            snapshot.Exists = exists;
            snapshot.Health = health;
            snapshot.Error = error == null ? string.Empty : error;
            snapshot.UpdatedUtc = DateTime.UtcNow;
            return snapshot;
        }

        private static ProviderResult CreateResult(PrSnapshot snapshot)
        {
            ProviderResult result = new ProviderResult();
            result.ProviderId = ProviderIdValue;
            result.Snapshot = snapshot;
            result.Health = snapshot.Health;
            result.Error = snapshot.Error;
            result.UpdatedUtc = DateTime.UtcNow;
            return result;
        }

        private static int NormalizePollSeconds(int seconds)
        {
            if (seconds <= 0)
            {
                return DefaultPollSeconds;
            }

            if (seconds < MinPollSeconds)
            {
                return MinPollSeconds;
            }

            if (seconds > MaxPollSeconds)
            {
                return MaxPollSeconds;
            }

            return seconds;
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
