using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// 端口 / 本地服务数据源：枚举 LISTEN 状态的 TCP 端口 → PID → 进程名。
    /// 只取数，不格式化；端口为 0 个时由 Presenter 决定隐藏该段。
    /// </summary>
    internal sealed class PortProvider : IProvider
    {
        internal const string ProviderIdValue = "ports";
        private const int DefaultPollSeconds = 5;
        private const int MinPollSeconds = 2;
        private const int MaxPollSeconds = 600;

        private readonly object _sync = new object();
        private int[] _watchPorts;
        private int _pollSeconds;
        private bool _disposed;

        internal PortProvider(int[] watchPorts, int pollSeconds)
        {
            _watchPorts = CopyPorts(watchPorts);
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
            get { return TimeSpan.FromSeconds(3); }
        }

        internal void UpdateSettings(int[] watchPorts, int pollSeconds)
        {
            lock (_sync)
            {
                _watchPorts = CopyPorts(watchPorts);
                _pollSeconds = NormalizePollSeconds(pollSeconds);
            }
        }

        public Task<ProviderResult> QueryAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            int[] watchPorts;
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException(token);
                }

                watchPorts = CopyPorts(_watchPorts);
            }

            PortSnapshot snapshot = new PortSnapshot();
            snapshot.WatchPorts = watchPorts;
            snapshot.WatchAll = watchPorts.Length == 0;
            snapshot.UpdatedUtc = DateTime.UtcNow;
            snapshot.Health = SnapshotHealth.Live;
            snapshot.Error = string.Empty;

            List<PortEntry> all = PortScanner.GetListeningPorts();
            List<PortEntry> filtered = PortScanner.Filter(all, watchPorts);
            snapshot.Ports = filtered.ToArray();

            Log.Debug(
                "端口查询完成：监听 "
                + all.Count.ToString()
                + " 个，匹配 "
                + snapshot.Ports.Length.ToString()
                + " 个");

            return Task.FromResult(CreateResult(snapshot));
        }

        private static ProviderResult CreateResult(PortSnapshot snapshot)
        {
            ProviderResult result = new ProviderResult();
            result.ProviderId = ProviderIdValue;
            result.Snapshot = snapshot;
            result.Health = snapshot.Health;
            result.Error = snapshot.Error;
            result.UpdatedUtc = DateTime.UtcNow;
            return result;
        }

        internal static int[] CopyPorts(int[] ports)
        {
            if (ports == null || ports.Length == 0)
            {
                return new int[0];
            }

            List<int> result = new List<int>();
            for (int index = 0; index < ports.Length; index++)
            {
                int port = ports[index];
                if (port > 0 && port <= 65535 && !result.Contains(port))
                {
                    result.Add(port);
                }
            }

            return result.ToArray();
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
