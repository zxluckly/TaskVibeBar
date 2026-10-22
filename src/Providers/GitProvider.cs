using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// Git 状态数据源。只读取 settings.git.repoPath 指定的仓库，通过
    /// `git status --porcelain=v2 --branch` 产出 GitSnapshot；不引用任何
    /// System.Windows.*。文件监听只用于加速刷新，失败时自动降级为轮询。
    /// </summary>
    internal sealed class GitProvider : IProvider, IDisposable
    {
        internal const string ProviderIdValue = "git";
        internal const int CommandTimeoutMilliseconds = 5000;
        private const int DefaultPollSeconds = 30;
        private const int MinPollSeconds = 10;
        private const int MaxPollSeconds = 86400;
        private const int DebounceMilliseconds = 500;
        private const int WatcherBufferBytes = 64 * 1024;
        private const int MaxErrorLength = 200;

        private readonly object _sync = new object();
        private Timer _debounceTimer;

        private string _repoPath;
        private bool _includeUntracked;
        private int _pollSeconds;
        private string _gitDir;
        private FileSystemWatcher _watcher;
        private FileSystemWatcher _worktreeWatcher;
        private Process _activeProcess;
        private bool _disposed;

        /// <summary>
        /// 文件监听触发时在后台线程抛出；AppController 收到后调用
        /// ProviderScheduler.Refresh("git")。不改变 IProvider 契约。
        /// </summary>
        internal event Action RefreshSuggested;

        internal GitProvider(string repoPath, bool includeUntracked, int pollSeconds)
        {
            _repoPath = NormalizeRepoPath(repoPath);
            _includeUntracked = includeUntracked;
            _pollSeconds = NormalizePollSeconds(pollSeconds);
            _gitDir = ResolveGitDir(_repoPath);

            _debounceTimer = new Timer(
                OnDebounceElapsed,
                null,
                System.Threading.Timeout.Infinite,
                System.Threading.Timeout.Infinite);

            lock (_sync)
            {
                RebuildWatcherLocked();
            }
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
            get { return TimeSpan.FromMilliseconds(CommandTimeoutMilliseconds); }
        }

        /// <summary>
        /// 设置保存后更新仓库、未跟踪文件策略与兜底轮询间隔，并按新的仓库
        /// 重建 FileSystemWatcher。调用方随后应触发一次 Refresh。
        /// </summary>
        internal void UpdateSettings(string repoPath, bool includeUntracked, int pollSeconds)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                string normalized = NormalizeRepoPath(repoPath);
                bool repoChanged = !string.Equals(
                    normalized,
                    _repoPath,
                    StringComparison.OrdinalIgnoreCase);

                _repoPath = normalized;
                _includeUntracked = includeUntracked;
                _pollSeconds = NormalizePollSeconds(pollSeconds);
                _gitDir = ResolveGitDir(normalized);

                // 仓库没变且 watcher 仍然可用时无需重建；此前创建失败
                // （路径不存在/无权限）则再试一次。
                if (repoChanged || _watcher == null || _worktreeWatcher == null)
                {
                    RebuildWatcherLocked();
                }
            }
        }

        public Task<ProviderResult> QueryAsync(CancellationToken token)
        {
            return QueryCoreAsync(token);
        }

        private async Task<ProviderResult> QueryCoreAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            string repoPath;
            bool includeUntracked;
            string gitDir;
            bool disposed;

            lock (_sync)
            {
                repoPath = _repoPath;
                includeUntracked = _includeUntracked;
                gitDir = _gitDir;
                disposed = _disposed;
            }

            if (disposed)
            {
                throw new OperationCanceledException(token);
            }

            if (string.IsNullOrEmpty(repoPath))
            {
                Log.Debug("Git 查询跳过：未选择仓库");
                return CreateResult(CreateUnavailableSnapshot(repoPath, "未选择仓库"));
            }

            if (!Directory.Exists(repoPath))
            {
                Log.Write("Git 查询失败：仓库路径不存在");
                return CreateResult(CreateUnavailableSnapshot(repoPath, "仓库路径不存在"));
            }

            GitRunResult run = await RunGitAsync(repoPath, includeUntracked, token)
                .ConfigureAwait(false);

            if (run.Cancelled || token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }

            if (run.GitNotFound)
            {
                Log.Write("Git 查询失败：未找到 git");
                return CreateResult(CreateUnavailableSnapshot(repoPath, "未找到 git"));
            }

            if (run.TimedOut)
            {
                Log.Write("Git 查询失败：git 命令超时");
                return CreateResult(CreateErrorSnapshot(repoPath, "git 命令超时"));
            }

            if (run.ExitCode != 0)
            {
                string stderrLine = FirstLine(run.Stderr, MaxErrorLength);
                if (ContainsNotARepository(stderrLine))
                {
                    Log.Write("Git 查询失败：不是 Git 仓库");
                    return CreateResult(
                        CreateUnavailableSnapshot(repoPath, "不是 Git 仓库"));
                }

                string error = string.IsNullOrEmpty(stderrLine)
                    ? "git 命令失败（退出码 "
                        + run.ExitCode.ToString(CultureInfo.InvariantCulture)
                        + "）"
                    : "git 命令失败：" + stderrLine;
                Log.Write("Git 查询失败：" + error);
                return CreateResult(CreateErrorSnapshot(repoPath, error));
            }

            GitSnapshot snapshot;
            try
            {
                snapshot = ParseStatus(run.Stdout, repoPath);
            }
            catch (Exception exception)
            {
                Log.Write("Git 输出解析失败：" + exception.GetType().Name);
                return CreateResult(CreateErrorSnapshot(repoPath, "git 输出解析失败"));
            }

            if (string.IsNullOrEmpty(gitDir))
            {
                gitDir = ResolveGitDir(repoPath);
                lock (_sync)
                {
                    if (!_disposed && string.Equals(repoPath, _repoPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _gitDir = gitDir;
                    }
                }
            }

            snapshot.Operation = DetectOperation(gitDir);
            snapshot.UpdatedUtc = DateTime.UtcNow;
            snapshot.Health = SnapshotHealth.Live;
            snapshot.Error = string.Empty;

            Log.Debug(
                "Git 查询成功："
                + snapshot.Branch
                + "，脏 "
                + snapshot.DirtyCount.ToString(CultureInfo.InvariantCulture));
            return CreateResult(snapshot);
        }

        private async Task<GitRunResult> RunGitAsync(
            string repoPath,
            bool includeUntracked,
            CancellationToken token)
        {
            GitRunResult result = new GitRunResult();

            Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = "git";
            startInfo.Arguments = BuildArguments(repoPath, includeUntracked);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = new UTF8Encoding(false);
            startInfo.StandardErrorEncoding = new UTF8Encoding(false);
            // 禁止 git status 为刷新 stat 信息而写回 .git/index：
            // 否则 .git 的 FileSystemWatcher 会再次触发查询，形成自激循环。
            startInfo.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.EnvironmentVariables["GIT_PAGER"] = "cat";

            TaskCompletionSource<bool> exitSource = new TaskCompletionSource<bool>();
            CancellationTokenRegistration registration = new CancellationTokenRegistration();
            Task<string> stdoutTask = null;
            Task<string> stderrTask = null;

            try
            {
                process.StartInfo = startInfo;

                try
                {
                    if (!process.Start())
                    {
                        result.GitNotFound = true;
                        return result;
                    }
                }
                catch (Win32Exception)
                {
                    result.GitNotFound = true;
                    return result;
                }
                catch (InvalidOperationException)
                {
                    result.GitNotFound = true;
                    return result;
                }

                AttachProcess(process);

                process.EnableRaisingEvents = true;
                process.Exited += delegate { exitSource.TrySetResult(true); };
                if (process.HasExited)
                {
                    exitSource.TrySetResult(true);
                }

                stdoutTask = process.StandardOutput.ReadToEndAsync();
                stderrTask = process.StandardError.ReadToEndAsync();

                registration = token.Register(
                    delegate
                    {
                        TryKill(process);
                    });

                Task completed = await Task.WhenAny(
                    exitSource.Task,
                    Task.Delay(CommandTimeoutMilliseconds)).ConfigureAwait(false);

                if (completed != exitSource.Task)
                {
                    result.TimedOut = true;
                    TryKill(process);
                    try
                    {
                        process.WaitForExit(1000);
                    }
                    catch
                    {
                    }

                    ObserveFault(stdoutTask);
                    ObserveFault(stderrTask);
                    return result;
                }

                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    TryKill(process);
                    ObserveFault(stdoutTask);
                    ObserveFault(stderrTask);
                    return result;
                }

                try
                {
                    result.ExitCode = process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    result.ExitCode = -1;
                }

                Task allOutput = Task.WhenAll(stdoutTask, stderrTask);
                await Task.WhenAny(
                    allOutput,
                    Task.Delay(1000)).ConfigureAwait(false);

                if (allOutput.IsCompleted)
                {
                    result.Stdout = stdoutTask.Result;
                    result.Stderr = stderrTask.Result;
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                TryKill(process);
                throw;
            }
            finally
            {
                registration.Dispose();
                DetachProcess(process);
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
        }

        private static string BuildArguments(string repoPath, bool includeUntracked)
        {
            string argumentPath = repoPath;
            // Windows 命令行中反斜杠会转义结尾引号；改用 <path>\. 既保留
            // 语义又避免 CreateProcess 解析错误。
            if (argumentPath.EndsWith("\\", StringComparison.Ordinal))
            {
                argumentPath = argumentPath + ".";
            }

            return "-C \""
                + argumentPath.Replace("\"", "\\\"")
                + "\" -c core.quotepath=false status --porcelain=v2 --branch --untracked-files="
                + (includeUntracked ? "normal" : "no");
        }

        /// <summary>
        /// 解析 `git status --porcelain=v2 --branch` 输出；供自测直接调用。
        /// </summary>
        internal static GitSnapshot ParseStatus(string output, string repoPath)
        {
            GitSnapshot snapshot = new GitSnapshot();
            snapshot.RepoPath = repoPath == null ? string.Empty : repoPath;
            snapshot.RepoName = GetRepoName(repoPath);
            snapshot.Branch = string.Empty;
            snapshot.Operation = string.Empty;
            snapshot.Health = SnapshotHealth.Live;
            snapshot.Error = string.Empty;
            snapshot.UpdatedUtc = DateTime.UtcNow;

            string oid = string.Empty;
            string normalized = output == null
                ? string.Empty
                : output.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    string head = line.Substring(14).Trim();
                    if (string.Equals(head, "(detached)", StringComparison.Ordinal))
                    {
                        snapshot.IsDetached = true;
                    }
                    else if (!string.Equals(head, "(unknown)", StringComparison.Ordinal))
                    {
                        snapshot.Branch = head;
                    }

                    continue;
                }

                if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
                {
                    oid = line.Substring(13).Trim();
                    continue;
                }

                if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    string[] tokens = line.Substring(12).Split(
                        new char[] { ' ' },
                        StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 2)
                    {
                        // 输出格式为 "+N -M"：Behind 取绝对值 M。
                        snapshot.Ahead = Math.Abs(ParseSignedInt(tokens[0]));
                        snapshot.Behind = Math.Abs(ParseSignedInt(tokens[1]));
                    }

                    continue;
                }

                if (line.StartsWith("1 ", StringComparison.Ordinal)
                    || line.StartsWith("2 ", StringComparison.Ordinal))
                {
                    if (line.Length >= 4)
                    {
                        if (line[2] != '.')
                        {
                            snapshot.StagedCount++;
                        }

                        if (line[3] != '.')
                        {
                            snapshot.ModifiedCount++;
                        }
                    }

                    continue;
                }

                if (line.StartsWith("u ", StringComparison.Ordinal))
                {
                    snapshot.ConflictCount++;
                    continue;
                }

                if (line.StartsWith("? ", StringComparison.Ordinal))
                {
                    snapshot.UntrackedCount++;
                }
            }

            if (snapshot.IsDetached)
            {
                snapshot.Branch = ShortOid(oid);
            }

            return snapshot;
        }

        /// <summary>
        /// 检查 MERGE_HEAD / rebase / CHERRY_PICK_HEAD / REVERT_HEAD / BISECT_LOG。
        /// </summary>
        internal static string DetectOperation(string gitDir)
        {
            if (string.IsNullOrEmpty(gitDir) || !Directory.Exists(gitDir))
            {
                return string.Empty;
            }

            try
            {
                if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
                {
                    return "merge";
                }

                if (Directory.Exists(Path.Combine(gitDir, "rebase-merge"))
                    || Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
                {
                    return "rebase";
                }

                if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
                {
                    return "cherry-pick";
                }

                if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
                {
                    return "revert";
                }

                if (File.Exists(Path.Combine(gitDir, "BISECT_LOG")))
                {
                    return "bisect";
                }
            }
            catch (Exception exception)
            {
                Log.Debug("Git 操作状态检查失败：" + exception.GetType().Name);
            }

            return string.Empty;
        }

        internal static string GetRepoName(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath))
            {
                return string.Empty;
            }

            string trimmed = repoPath.TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return repoPath;
            }

            try
            {
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? trimmed : name;
            }
            catch (ArgumentException)
            {
                return repoPath;
            }
        }

        internal static string FirstLine(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            int lineBreak = text.IndexOfAny(new char[] { '\r', '\n' });
            string line = lineBreak >= 0 ? text.Substring(0, lineBreak) : text;
            line = line.Trim();
            if (maxLength > 0 && line.Length > maxLength)
            {
                line = line.Substring(0, maxLength);
            }

            return line;
        }

        internal static string NormalizeRepoPath(string repoPath)
        {
            if (repoPath == null)
            {
                return string.Empty;
            }

            string path = repoPath.Trim().Trim('"');
            if (path.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // 保留用户输入，后续由 Directory.Exists 判定。
            }

            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 2
                && trimmed[1] == ':')
            {
                return trimmed + "\\";
            }

            return trimmed.Length == 0 ? path : trimmed;
        }

        internal static string ResolveGitDir(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath))
            {
                return null;
            }

            try
            {
                string dotGit = Path.Combine(repoPath, ".git");
                if (Directory.Exists(dotGit))
                {
                    return dotGit;
                }

                if (File.Exists(dotGit))
                {
                    string firstLine = FirstLine(
                        File.ReadAllText(dotGit, Encoding.UTF8),
                        4096);
                    if (firstLine.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        string target = firstLine.Substring(7).Trim();
                        if (target.Length == 0)
                        {
                            return null;
                        }

                        if (!Path.IsPathRooted(target))
                        {
                            target = Path.Combine(repoPath, target);
                        }

                        return Path.GetFullPath(target);
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Write("读取 .git 失败：" + exception.GetType().Name);
            }

            return null;
        }

        private static int ParseSignedInt(string token)
        {
            int value;
            if (int.TryParse(
                token,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out value))
            {
                return value;
            }

            return 0;
        }

        private static string ShortOid(string oid)
        {
            if (string.IsNullOrEmpty(oid) || oid.StartsWith("(", StringComparison.Ordinal))
            {
                return "detached";
            }

            return oid.Length > 7 ? oid.Substring(0, 7) : oid;
        }

        private static bool ContainsNotARepository(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return text.IndexOf(
                "not a git repository",
                StringComparison.OrdinalIgnoreCase) >= 0;
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

        private void RebuildWatcherLocked()
        {
            _watcher = DisposeWatcher(_watcher);
            _worktreeWatcher = DisposeWatcher(_worktreeWatcher);

            string gitDir = _gitDir;
            if (string.IsNullOrEmpty(gitDir)
                || !Directory.Exists(gitDir)
                || string.IsNullOrEmpty(_repoPath)
                || !Directory.Exists(_repoPath))
            {
                Log.Debug("Git 文件监听不可用，降级为轮询");
                return;
            }

            _watcher = CreateWatcher(gitDir, "Git 元数据监听");
            // .git 只覆盖 HEAD/index/refs；工作区普通文件修改与新建不会写
            // .git，因此额外监听工作区（排除 .git）以满足 2 秒刷新要求。
            _worktreeWatcher = CreateWatcher(_repoPath, "Git 工作区监听");
        }

        private FileSystemWatcher CreateWatcher(string path, string description)
        {
            try
            {
                FileSystemWatcher watcher = new FileSystemWatcher(path);
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime;
                watcher.InternalBufferSize = WatcherBufferBytes;
                watcher.Changed += OnFileSystemChanged;
                watcher.Created += OnFileSystemChanged;
                watcher.Deleted += OnFileSystemChanged;
                watcher.Renamed += OnFileSystemRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception exception)
            {
                Log.Write(
                    description
                    + "创建失败，使用轮询兜底："
                    + exception.GetType().Name);
                return null;
            }
        }

        private static FileSystemWatcher DisposeWatcher(FileSystemWatcher watcher)
        {
            if (watcher == null)
            {
                return null;
            }

            try
            {
                watcher.EnableRaisingEvents = false;
            }
            catch
            {
            }

            try
            {
                watcher.Dispose();
            }
            catch
            {
            }

            return null;
        }

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            if (sender == _worktreeWatcher
                && e != null
                && IsInsideGitDir(e.FullPath))
            {
                return;
            }

            ScheduleDebouncedRefresh();
        }

        private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
        {
            if (sender == _worktreeWatcher
                && e != null
                && IsInsideGitDir(e.FullPath))
            {
                return;
            }

            ScheduleDebouncedRefresh();
        }

        private bool IsInsideGitDir(string path)
        {
            string gitDir = _gitDir;
            if (string.IsNullOrEmpty(gitDir)
                || string.IsNullOrEmpty(path))
            {
                return false;
            }

            string prefix = gitDir.TrimEnd('\\', '/') + "\\";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private void ScheduleDebouncedRefresh()
        {
            lock (_sync)
            {
                if (_disposed || _debounceTimer == null)
                {
                    return;
                }

                try
                {
                    _debounceTimer.Change(
                        DebounceMilliseconds,
                        System.Threading.Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void OnDebounceElapsed(object state)
        {
            Action handler;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                handler = RefreshSuggested;
            }

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
                Log.Write("Git 刷新事件处理失败：" + exception.GetType().Name);
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Exception exception = e == null ? null : e.GetException();
            Log.Write(
                "Git 文件监听异常，降级为轮询："
                + (exception == null ? "未知错误" : exception.GetType().Name));

            FileSystemWatcher watcher = sender as FileSystemWatcher;
            if (watcher == null)
            {
                return;
            }

            try
            {
                watcher.EnableRaisingEvents = false;
            }
            catch
            {
            }

            try
            {
                watcher.Dispose();
            }
            catch
            {
            }

            lock (_sync)
            {
                if (_watcher == watcher)
                {
                    _watcher = null;
                }

                if (_worktreeWatcher == watcher)
                {
                    _worktreeWatcher = null;
                }
            }
        }

        private void AttachProcess(Process process)
        {
            bool kill = false;
            lock (_sync)
            {
                if (_disposed)
                {
                    kill = true;
                }
                else
                {
                    _activeProcess = process;
                }
            }

            if (kill)
            {
                TryKill(process);
            }
        }

        private void DetachProcess(Process process)
        {
            lock (_sync)
            {
                if (_activeProcess == process)
                {
                    _activeProcess = null;
                }
            }
        }

        private static void TryKill(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Win32Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        private static void ObserveFault(Task task)
        {
            if (task == null)
            {
                return;
            }

            task.ContinueWith(
                delegate(Task completed)
                {
                    Exception ignored = completed.Exception;
                },
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private static GitSnapshot CreateUnavailableSnapshot(string repoPath, string error)
        {
            return CreateSnapshot(repoPath, error, SnapshotHealth.Unavailable);
        }

        private static GitSnapshot CreateErrorSnapshot(string repoPath, string error)
        {
            return CreateSnapshot(repoPath, error, SnapshotHealth.Error);
        }

        private static GitSnapshot CreateSnapshot(
            string repoPath,
            string error,
            SnapshotHealth health)
        {
            GitSnapshot snapshot = new GitSnapshot();
            snapshot.RepoPath = repoPath == null ? string.Empty : repoPath;
            snapshot.RepoName = GetRepoName(repoPath);
            snapshot.Branch = string.Empty;
            snapshot.IsDetached = false;
            snapshot.Operation = string.Empty;
            snapshot.UpdatedUtc = DateTime.UtcNow;
            snapshot.Health = health;
            snapshot.Error = error == null ? string.Empty : error;
            return snapshot;
        }

        private static ProviderResult CreateResult(GitSnapshot snapshot)
        {
            ProviderResult result = new ProviderResult();
            result.ProviderId = ProviderIdValue;
            result.Snapshot = snapshot;
            result.Health = snapshot.Health;
            result.Error = snapshot.Error;
            result.UpdatedUtc = DateTime.UtcNow;
            return result;
        }

        public void Dispose()
        {
            FileSystemWatcher watcher;
            FileSystemWatcher worktreeWatcher;
            Timer timer;
            Process process;

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                watcher = _watcher;
                _watcher = null;
                worktreeWatcher = _worktreeWatcher;
                _worktreeWatcher = null;
                timer = _debounceTimer;
                _debounceTimer = null;
                process = _activeProcess;
                _activeProcess = null;
            }

            if (watcher != null)
            {
                watcher.Changed -= OnFileSystemChanged;
                watcher.Created -= OnFileSystemChanged;
                watcher.Deleted -= OnFileSystemChanged;
                watcher.Renamed -= OnFileSystemRenamed;
                watcher.Error -= OnWatcherError;
                DisposeWatcher(watcher);
            }

            if (worktreeWatcher != null)
            {
                worktreeWatcher.Changed -= OnFileSystemChanged;
                worktreeWatcher.Created -= OnFileSystemChanged;
                worktreeWatcher.Deleted -= OnFileSystemChanged;
                worktreeWatcher.Renamed -= OnFileSystemRenamed;
                worktreeWatcher.Error -= OnWatcherError;
                DisposeWatcher(worktreeWatcher);
            }

            if (timer != null)
            {
                try
                {
                    timer.Dispose();
                }
                catch
                {
                }
            }

            if (process != null)
            {
                TryKill(process);
            }
        }

        private sealed class GitRunResult
        {
            internal string Stdout = string.Empty;
            internal string Stderr = string.Empty;
            internal int ExitCode;
            internal bool TimedOut;
            internal bool GitNotFound;
            internal bool Cancelled;
        }
    }
}
