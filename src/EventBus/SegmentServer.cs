using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// 外部段命名管道服务端（VibeBar.Segment.v1）。与 Hook IPC（VibeBar.Events.v1）
    /// 完全独立：独立管道名、独立后台线程，互不影响。
    ///
    /// 并发模型：后台 accept 线程只负责「建实例 → 等连接 → 交出去」，
    /// 连接建立后立刻把该实例交给线程池处理，主循环马上建下一个实例，
    /// 因此一个工具用长连接心跳不会把其他工具挡在门外。
    /// 最多同时 8 个连接（8 个管道实例），满了就退避等待。
    /// 单连接内按 '\n' 切帧；每帧回一行 {"ok":...}。
    /// 任何解析/IO 异常只记 Debug 日志，绝不冒泡到 UI 线程。
    /// </summary>
    internal sealed class SegmentServer : IDisposable
    {
        internal const int MaxConcurrentClients = 8;

        // 长连接允许空闲，但必须放弃「连上不发数据」的客户端，
        // 否则管道实例会被永久占用。
        private const int IdleTimeoutMilliseconds = 30000;
        private const int AcceptPollMilliseconds = 500;
        private const int FullBackoffMilliseconds = 50;
        private const int ErrorSleepMilliseconds = 500;

        // 停止时只做短暂等待：Stop() 可能在 UI 线程被调用（热重载关总开关），
        // 管道实例在等待之前已经全部关闭，处理线程会立即失败退出。
        private const int StopJoinMilliseconds = 200;

        private const int ReadBufferSize = 1024;
        private const int FallbackTtlSeconds = 30;

        private readonly object _sync = new object();
        private readonly Func<ExternalSegmentFrame, string> _handler;
        private readonly int _defaultTtlSeconds;
        private readonly List<NamedPipeServerStream> _servers =
            new List<NamedPipeServerStream>();

        private Thread _thread;
        private object _session;
        private int _instanceCount;
        private int _activeConnections;
        private bool _disposed;

        internal SegmentServer(Func<ExternalSegmentFrame, string> handler)
            : this(handler, FallbackTtlSeconds)
        {
        }

        /// <summary>
        /// defaultTtlSeconds 来自 settings.external.defaultTtlSeconds（已夹取），
        /// 用于帧里缺省 ttl 时的兜底。
        /// </summary>
        internal SegmentServer(
            Func<ExternalSegmentFrame, string> handler,
            int defaultTtlSeconds)
        {
            if (handler == null)
            {
                throw new ArgumentNullException("handler");
            }

            _handler = handler;
            _defaultTtlSeconds = SegmentProtocol.ClampTtl(defaultTtlSeconds);
        }

        internal int DefaultTtlSeconds
        {
            get { return _defaultTtlSeconds; }
        }

        internal void Start()
        {
            lock (_sync)
            {
                if (_disposed || _thread != null)
                {
                    return;
                }

                object session = new object();
                _session = session;

                Thread thread = new Thread(Run);
                thread.IsBackground = true;
                thread.Name = "VibeBar.SegmentServer";
                _thread = thread;
                thread.Start();
            }
        }

        /// <summary>
        /// 停止监听：关闭所有活跃管道实例（等待连接的 + 已连接的），并最多等 200ms
        /// 让 accept 线程与处理线程退出。可再次 Start()。
        /// </summary>
        internal void Stop()
        {
            NamedPipeServerStream[] servers;
            Thread thread;

            lock (_sync)
            {
                _session = null;
                servers = _servers.ToArray();
                _servers.Clear();
                thread = _thread;
                _thread = null;
            }

            Interlocked.Exchange(ref _instanceCount, 0);

            for (int index = 0; index < servers.Length; index++)
            {
                CloseQuietly(servers[index]);
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(StopJoinMilliseconds);

            if (thread != null && thread != Thread.CurrentThread)
            {
                int remaining = RemainingMilliseconds(deadline);
                if (remaining > 0)
                {
                    try
                    {
                        thread.Join(remaining);
                    }
                    catch (ThreadStateException)
                    {
                        // 线程已退出，忽略。
                    }
                }
            }

            // 处理线程是线程池线程，不能 Join；管道已关闭，它们会立即失败退出。
            while (Volatile.Read(ref _activeConnections) > 0
                && RemainingMilliseconds(deadline) > 0)
            {
                try
                {
                    Thread.Sleep(10);
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
            }
        }

        private void Run()
        {
            object session;
            lock (_sync)
            {
                session = _session;
            }

            while (IsSessionActive(session))
            {
                if (Volatile.Read(ref _instanceCount) >= MaxConcurrentClients)
                {
                    // 8 个实例都在用：不再建新实例，退避避免空转。
                    SleepQuietly(FullBackoffMilliseconds);
                    continue;
                }

                NamedPipeServerStream server = null;
                bool handedOff = false;
                try
                {
                    server = CreateServerPipe();
                    if (!RegisterServer(server, session))
                    {
                        CloseServer(server);
                        return;
                    }

                    if (!WaitForConnection(server, session))
                    {
                        CloseServer(server);
                        continue;
                    }

                    if (!IsSessionActive(session))
                    {
                        CloseServer(server);
                        return;
                    }

                    // 连接已建立：实例所有权移交线程池，主循环立刻建下一个实例。
                    Interlocked.Increment(ref _activeConnections);
                    ConnectionState state = new ConnectionState();
                    state.Server = server;
                    state.Session = session;
                    handedOff = ThreadPool.QueueUserWorkItem(
                        HandleClientWorkItem,
                        state);
                    if (!handedOff)
                    {
                        // 线程池拒绝（极罕见）：回滚计数并关闭该连接。
                        Interlocked.Decrement(ref _activeConnections);
                        CloseServer(server);
                    }
                }
                catch (ObjectDisposedException)
                {
                    if (!handedOff)
                    {
                        CloseServer(server);
                    }

                    if (!IsSessionActive(session))
                    {
                        return;
                    }

                    Log.Debug("外部段管道已释放");
                }
                catch (IOException exception)
                {
                    if (!handedOff)
                    {
                        CloseServer(server);
                    }

                    if (!IsSessionActive(session))
                    {
                        return;
                    }

                    Log.Debug("外部段管道 IO 异常：" + exception.GetType().Name);
                    SleepQuietly(ErrorSleepMilliseconds);
                }
                catch (Exception exception)
                {
                    if (!handedOff)
                    {
                        CloseServer(server);
                    }

                    if (!IsSessionActive(session))
                    {
                        return;
                    }

                    Log.Write("外部段管道异常：" + exception.GetType().Name);
                    SleepQuietly(ErrorSleepMilliseconds);
                }
            }
        }

        /// <summary>
        /// 管道 ACL 与 EventServer 保持一致：仅当前 Windows 用户可连接
        /// （.NET Framework 4.8 的 PipeOptions 没有 CurrentUserOnly）。
        /// 实例数 = MaxConcurrentClients，多个客户端可以同时连上。
        /// </summary>
        private static NamedPipeServerStream CreateServerPipe()
        {
            PipeSecurity security = new PipeSecurity();
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (identity != null && identity.User != null)
            {
                security.AddAccessRule(new PipeAccessRule(
                    identity.User,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));
            }

            return new NamedPipeServerStream(
                SegmentProtocol.PipeName,
                PipeDirection.InOut,
                MaxConcurrentClients,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                security);
        }

        private bool RegisterServer(NamedPipeServerStream server, object session)
        {
            lock (_sync)
            {
                if (!IsSessionActiveLocked(session))
                {
                    return false;
                }

                _servers.Add(server);
                Interlocked.Increment(ref _instanceCount);
                return true;
            }
        }

        /// <summary>
        /// 关闭一个实例并归还并发额度。幂等：只有仍在登记表里的实例才减计数。
        /// </summary>
        private void CloseServer(NamedPipeServerStream server)
        {
            if (server == null)
            {
                return;
            }

            bool removed;
            lock (_sync)
            {
                removed = _servers.Remove(server);
            }

            if (removed)
            {
                Interlocked.Decrement(ref _instanceCount);
            }

            CloseQuietly(server);
        }

        /// <summary>
        /// 等待连接建立。不无限阻塞：每 500ms 检查一次会话是否还有效，
        /// Stop() 关闭实例时也会立刻让等待失败返回。
        /// </summary>
        private bool WaitForConnection(NamedPipeServerStream server, object session)
        {
            Task task;
            try
            {
                task = server.WaitForConnectionAsync();
            }
            catch (Exception exception)
            {
                Log.Debug("外部段等待连接启动失败：" + exception.GetType().Name);
                return false;
            }

            while (true)
            {
                try
                {
                    if (task.Wait(AcceptPollMilliseconds))
                    {
                        return true;
                    }
                }
                catch (AggregateException exception)
                {
                    Exception inner = exception.GetBaseException();
                    if (!(inner is IOException) && !(inner is ObjectDisposedException))
                    {
                        Log.Debug("外部段接受连接失败：" + inner.GetType().Name);
                    }

                    return false;
                }
                catch (Exception exception)
                {
                    Log.Debug("外部段接受连接失败：" + exception.GetType().Name);
                    return false;
                }

                if (!IsSessionActive(session))
                {
                    ObserveFault(task);
                    return false;
                }
            }
        }

        private void HandleClientWorkItem(object stateObject)
        {
            ConnectionState state = stateObject as ConnectionState;
            if (state == null)
            {
                return;
            }

            try
            {
                if (IsSessionActive(state.Session))
                {
                    HandleClient(state.Server, state.Session);
                }
            }
            catch (Exception exception)
            {
                Log.Debug("外部段连接处理异常：" + exception.GetType().Name);
            }
            finally
            {
                CloseServer(state.Server);
                Interlocked.Decrement(ref _activeConnections);
            }
        }

        private void HandleClient(NamedPipeServerStream server, object session)
        {
            byte[] buffer = new byte[ReadBufferSize];
            MemoryStream frame = new MemoryStream(SegmentProtocol.MaxFrameBytes);
            bool overflow = false;

            // 限速按连接独立计数：每个连接各自 20 帧/秒。
            DateTime windowStartUtc = DateTime.UtcNow;
            int framesInWindow = 0;

            try
            {
                while (IsSessionActive(session))
                {
                    int read;
                    if (!TryRead(server, buffer, out read))
                    {
                        return;
                    }

                    if (read <= 0)
                    {
                        return;
                    }

                    for (int index = 0; index < read; index++)
                    {
                        byte current = buffer[index];
                        if (current != (byte)'\n')
                        {
                            if (overflow)
                            {
                                continue;
                            }

                            if (frame.Length >= SegmentProtocol.MaxFrameBytes)
                            {
                                // 单帧超过 4KB：丢弃到下一个换行，回 invalid_json。
                                overflow = true;
                                frame.SetLength(0);
                                continue;
                            }

                            frame.WriteByte(current);
                            continue;
                        }

                        byte[] bytes = frame.ToArray();
                        frame.SetLength(0);
                        bool wasOverflow = overflow;
                        overflow = false;

                        if (!IsSessionActive(session))
                        {
                            return;
                        }

                        DateTime nowUtc = DateTime.UtcNow;
                        if ((nowUtc - windowStartUtc).TotalSeconds >= 1.0)
                        {
                            windowStartUtc = nowUtc;
                            framesInWindow = 0;
                        }

                        framesInWindow++;
                        if (framesInWindow > SegmentProtocol.MaxFramesPerSecond)
                        {
                            if (!TryWrite(server, BuildReply(SegmentProtocol.ErrorRateLimited)))
                            {
                                return;
                            }

                            continue;
                        }

                        if (wasOverflow)
                        {
                            Log.Debug("外部段帧超过 4KB，已丢弃");
                            if (!TryWrite(server, BuildReply(SegmentProtocol.ErrorInvalidJson)))
                            {
                                return;
                            }

                            continue;
                        }

                        if (!TryWrite(server, ProcessFrame(bytes)))
                        {
                            return;
                        }
                    }
                }
            }
            finally
            {
                frame.Dispose();
            }
        }

        private string ProcessFrame(byte[] bytes)
        {
            int offset = 0;
            if (bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF)
            {
                offset = 3;
            }

            string line;
            try
            {
                line = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (Exception exception)
            {
                Log.Debug("外部段帧解码失败：" + exception.GetType().Name);
                return BuildReply(SegmentProtocol.ErrorInvalidJson);
            }

            // Windows 客户端常用 WriteLine，行尾带 \r。
            line = line.TrimEnd('\r');

            ExternalSegmentFrame frame;
            string error;
            if (!SegmentProtocol.TryParse(line, _defaultTtlSeconds, out frame, out error))
            {
                Log.Debug("外部段帧解析失败：" + error);
                return BuildReply(error);
            }

            string result;
            try
            {
                result = _handler(frame);
            }
            catch (Exception exception)
            {
                Log.Debug("外部段帧处理失败：" + exception.GetType().Name);
                return BuildReply(SegmentProtocol.ErrorInvalidJson);
            }

            if (string.IsNullOrEmpty(result)
                || string.Equals(result, SegmentProtocol.OkReply, StringComparison.Ordinal))
            {
                return BuildReply(null);
            }

            return BuildReply(result);
        }

        private static string BuildReply(string error)
        {
            string body = string.IsNullOrEmpty(error)
                ? SegmentProtocol.BuildOkReply()
                : SegmentProtocol.BuildErrorReply(error);
            return body + "\n";
        }

        private static bool TryWrite(NamedPipeServerStream server, string reply)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(reply);
                server.Write(bytes, 0, bytes.Length);
                server.Flush();
                return true;
            }
            catch (Exception exception)
            {
                Log.Debug("外部段回写失败：" + exception.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 在空闲超时内读取一块数据。ReadAsync 让「连上不发数据」的客户端
        /// 超时后被放弃，随后该实例被关闭，不会永久占用并发额度。
        /// </summary>
        private static bool TryRead(
            NamedPipeServerStream server,
            byte[] buffer,
            out int read)
        {
            read = 0;

            Task<int> task;
            try
            {
                task = server.ReadAsync(buffer, 0, buffer.Length);
            }
            catch (Exception exception)
            {
                Log.Debug("外部段读取启动失败：" + exception.GetType().Name);
                return false;
            }

            try
            {
                if (!task.Wait(IdleTimeoutMilliseconds))
                {
                    ObserveFault(task);
                    Log.Debug("外部段客户端空闲超时，已断开");
                    return false;
                }

                read = task.Result;
                return true;
            }
            catch (AggregateException exception)
            {
                Exception inner = exception.GetBaseException();
                if (!(inner is IOException) && !(inner is ObjectDisposedException))
                {
                    Log.Debug("外部段读取异常：" + inner.GetType().Name);
                }

                return false;
            }
            catch (Exception exception)
            {
                Log.Debug("外部段读取异常：" + exception.GetType().Name);
                return false;
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

        private bool IsSessionActive(object session)
        {
            lock (_sync)
            {
                return IsSessionActiveLocked(session);
            }
        }

        private bool IsSessionActiveLocked(object session)
        {
            return !_disposed && session != null && ReferenceEquals(_session, session);
        }

        private static int RemainingMilliseconds(DateTime deadlineUtc)
        {
            double remaining = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0)
            {
                return 0;
            }

            return remaining > int.MaxValue ? int.MaxValue : (int)remaining;
        }

        private static void SleepQuietly(int milliseconds)
        {
            try
            {
                Thread.Sleep(milliseconds);
            }
            catch (ThreadInterruptedException)
            {
            }
        }

        private static void CloseQuietly(NamedPipeServerStream server)
        {
            if (server == null)
            {
                return;
            }

            try
            {
                server.Dispose();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();
        }

        /// <summary>
        /// accept 线程 → 线程池处理单元的交接载体。
        /// </summary>
        private sealed class ConnectionState
        {
            internal NamedPipeServerStream Server;
            internal object Session;
        }
    }
}
