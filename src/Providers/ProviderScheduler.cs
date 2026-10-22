using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// Provider 轮询调度器：首次延迟 1s、按 PollInterval 轮询、单 Provider 禁止重入。
    /// 失败退避 1×→2×→4×→8×（上限 5 分钟），成功恢复原间隔。
    /// 单个 Provider 的超时/异常不会影响其他 Provider。
    /// 结果通过 ResultAvailable 事件在后台线程抛出，由 AppController 切回 UI。
    /// </summary>
    internal sealed class ProviderScheduler : IDisposable
    {
        private const int FirstDelayMilliseconds = 1000;
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan InfiniteWait = TimeSpan.FromMilliseconds(-1);

        private readonly object _sync = new object();
        private readonly List<Registration> _registrations = new List<Registration>();
        private bool _started;
        private bool _disposed;

        internal event Action<ProviderResult> ResultAvailable;

        internal void Register(IProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException("provider");
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                for (int index = 0; index < _registrations.Count; index++)
                {
                    if (string.Equals(
                        _registrations[index].Provider.Id,
                        provider.Id,
                        StringComparison.Ordinal))
                    {
                        Log.Write("Provider 重复注册，已忽略：" + provider.Id);
                        return;
                    }
                }

                Registration registration = new Registration();
                registration.Provider = provider;
                _registrations.Add(registration);

                if (_started)
                {
                    StartLoop(registration);
                }
            }
        }

        internal void Start()
        {
            lock (_sync)
            {
                if (_started || _disposed)
                {
                    return;
                }

                _started = true;
                for (int index = 0; index < _registrations.Count; index++)
                {
                    StartLoop(_registrations[index]);
                }
            }
        }

        internal void RefreshAll()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                for (int index = 0; index < _registrations.Count; index++)
                {
                    Signal(_registrations[index]);
                }
            }
        }

        internal void Refresh(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                for (int index = 0; index < _registrations.Count; index++)
                {
                    Registration registration = _registrations[index];
                    if (string.Equals(
                        registration.Provider.Id,
                        id,
                        StringComparison.Ordinal))
                    {
                        Signal(registration);
                        return;
                    }
                }
            }
        }

        private static void Signal(Registration registration)
        {
            if (registration == null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref registration.Running, 0, 0) != 0)
            {
                // 同一 Provider 运行中则跳过。
                return;
            }

            Interlocked.Exchange(ref registration.FailureCount, 0);
            try
            {
                registration.Wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已有一次待处理的刷新请求。
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void StartLoop(Registration registration)
        {
            registration.Cancellation = new CancellationTokenSource();
            CancellationToken token = registration.Cancellation.Token;
            registration.Loop = Task.Run(async delegate
            {
                await LoopAsync(registration, token).ConfigureAwait(false);
            });
        }

        private async Task LoopAsync(Registration registration, CancellationToken token)
        {
            try
            {
                await Task.Delay(FirstDelayMilliseconds, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!token.IsCancellationRequested)
            {
                ProviderResult result = null;
                bool succeeded = false;

                if (Interlocked.CompareExchange(ref registration.Running, 1, 0) == 0)
                {
                    try
                    {
                        result = await ExecuteAsync(registration, token).ConfigureAwait(false);
                        succeeded = result != null && result.Health == SnapshotHealth.Live;
                    }
                    catch (OperationCanceledException)
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        result = CreateErrorResult(
                            registration.Provider.Id,
                            "请求超时");
                        succeeded = false;
                    }
                    catch (Exception exception)
                    {
                        Log.Write(
                            "Provider 执行异常："
                            + registration.Provider.Id
                            + "，"
                            + exception.GetType().Name);
                        result = CreateErrorResult(
                            registration.Provider.Id,
                            "数据源异常");
                        succeeded = false;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref registration.Running, 0);
                    }
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (result != null)
                {
                    RaiseResult(result);
                }

                TimeSpan delay = ComputeDelay(registration, succeeded);
                try
                {
                    if (delay == InfiniteWait)
                    {
                        await registration.Wake.WaitAsync(token).ConfigureAwait(false);
                    }
                    else
                    {
                        await registration.Wake.WaitAsync(delay, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private async Task<ProviderResult> ExecuteAsync(
            Registration registration,
            CancellationToken outerToken)
        {
            IProvider provider = registration.Provider;
            TimeSpan timeout = provider.Timeout;
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken);

            try
            {
                // Provider 自己负责 10s 超时；调度器再兜一层（留 2s 余量，
                // 让 Provider 有机会把超时转成带中文错误的快照）。
                if (timeout > TimeSpan.Zero
                    && timeout != TimeSpan.MaxValue
                    && timeout < TimeSpan.FromDays(1))
                {
                    linked.CancelAfter(timeout + TimeSpan.FromSeconds(2));
                }

                Task<ProviderResult> task = provider.QueryAsync(linked.Token);
                if (task == null)
                {
                    return CreateErrorResult(provider.Id, "数据源无响应");
                }

                ProviderResult result = await task.ConfigureAwait(false);
                if (result == null)
                {
                    return CreateErrorResult(provider.Id, "数据源无响应");
                }

                if (string.IsNullOrEmpty(result.ProviderId))
                {
                    result.ProviderId = provider.Id;
                }

                if (result.UpdatedUtc == default(DateTime))
                {
                    result.UpdatedUtc = DateTime.UtcNow;
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                if (outerToken.IsCancellationRequested)
                {
                    throw;
                }

                return CreateErrorResult(provider.Id, "请求超时");
            }
            catch (Exception exception)
            {
                Log.Write(
                    "Provider 查询失败："
                    + provider.Id
                    + "，"
                    + exception.GetType().Name);
                return CreateErrorResult(provider.Id, "数据源异常");
            }
            finally
            {
                linked.Dispose();
            }
        }

        private static TimeSpan ComputeDelay(Registration registration, bool succeeded)
        {
            TimeSpan interval = registration.Provider.PollInterval;
            if (interval <= TimeSpan.Zero || interval == TimeSpan.MaxValue
                || interval > TimeSpan.FromDays(1))
            {
                if (succeeded)
                {
                    // 事件驱动的 Provider 不轮询，只等待 Refresh 唤醒。
                    return InfiniteWait;
                }

                interval = TimeSpan.FromSeconds(60);
            }

            if (succeeded)
            {
                Interlocked.Exchange(ref registration.FailureCount, 0);
                return interval;
            }

            int failures = Interlocked.Increment(ref registration.FailureCount);
            int shift = Math.Min(failures - 1, 3);
            double ticks = interval.Ticks * (double)(1 << shift);
            TimeSpan delay = TimeSpan.FromTicks((long)ticks);
            if (delay > MaxBackoff)
            {
                delay = MaxBackoff;
            }

            return delay;
        }

        private void RaiseResult(ProviderResult result)
        {
            Action<ProviderResult> handler = ResultAvailable;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(result);
            }
            catch (Exception exception)
            {
                Log.Write("Provider 结果回调异常：" + exception.GetType().Name);
            }
        }

        private static ProviderResult CreateErrorResult(string providerId, string error)
        {
            ProviderResult result = new ProviderResult();
            result.ProviderId = providerId;
            result.Snapshot = null;
            result.Health = SnapshotHealth.Error;
            result.Error = error;
            result.UpdatedUtc = DateTime.UtcNow;
            return result;
        }

        internal void Stop()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                for (int index = 0; index < _registrations.Count; index++)
                {
                    Registration registration = _registrations[index];
                    try
                    {
                        if (registration.Cancellation != null)
                        {
                            registration.Cancellation.Cancel();
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    try
                    {
                        registration.Provider.Dispose();
                    }
                    catch (Exception exception)
                    {
                        Log.Write(
                            "释放 Provider 失败："
                            + registration.Provider.Id
                            + "，"
                            + exception.GetType().Name);
                    }
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private sealed class Registration
        {
            internal IProvider Provider;
            internal CancellationTokenSource Cancellation;
            internal SemaphoreSlim Wake = new SemaphoreSlim(0, 1);
            internal int Running;
            internal int FailureCount;
            internal Task Loop;
        }
    }
}
