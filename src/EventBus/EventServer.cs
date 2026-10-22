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
    /// 命名管道事件服务端。独立后台线程单连接串行处理：
    /// 每个连接只读取一行不超过 64KB 的 UTF-8 JSON 信封，
    /// 校验 v/kind/payload 后交给 AgentProvider.Ingest。
    /// 非法输入只记 Debug 日志，绝不让后台线程崩溃。
    /// </summary>
    internal sealed class EventServer : IDisposable
    {
        private const int ReadTimeoutMilliseconds = 2000;
        private const int ErrorSleepMilliseconds = 500;

        private readonly object _sync = new object();
        private readonly AgentProvider _agentProvider;
        private readonly string _pipeName;

        private Thread _thread;
        private NamedPipeServerStream _activeServer;
        private bool _disposed;

        internal EventServer(AgentProvider agentProvider)
            : this(agentProvider, EventProtocol.PipeName)
        {
        }

        /// <summary>
        /// 测试用重载：允许使用独立管道名，生产路径始终使用
        /// EventProtocol.PipeName（正式版只调用单参数构造函数）。
        /// </summary>
        internal EventServer(AgentProvider agentProvider, string pipeName)
        {
            if (agentProvider == null)
            {
                throw new ArgumentNullException("agentProvider");
            }

            _agentProvider = agentProvider;
            _pipeName = string.IsNullOrEmpty(pipeName)
                ? EventProtocol.PipeName
                : pipeName;
        }

        internal string PipeName
        {
            get { return _pipeName; }
        }

        internal void Start()
        {
            lock (_sync)
            {
                if (_disposed || _thread != null)
                {
                    return;
                }

                _thread = new Thread(Run);
                _thread.IsBackground = true;
                _thread.Name = "VibeBar.EventServer";
                _thread.Start();
            }
        }

        private void Run()
        {
            while (!IsDisposed())
            {
                NamedPipeServerStream server = null;
                try
                {
                    // .NET Framework 4.8 的 PipeOptions 没有 CurrentUserOnly
                    // （该值属于 .NET Core）；改用等价的当前用户 ACL，
                    // 仅允许当前 Windows 用户连接。
                    server = CreateServerPipe();

                    lock (_sync)
                    {
                        if (_disposed)
                        {
                            CloseQuietly(server);
                            return;
                        }

                        _activeServer = server;
                    }

                    server.WaitForConnection();
                    if (IsDisposed())
                    {
                        return;
                    }

                    HandleClient(server);
                }
                catch (ObjectDisposedException)
                {
                    if (IsDisposed())
                    {
                        return;
                    }

                    Log.Debug("事件管道已释放");
                }
                catch (IOException exception)
                {
                    if (IsDisposed())
                    {
                        return;
                    }

                    Log.Debug("事件管道 IO 异常：" + exception.GetType().Name);
                    SleepAfterError();
                }
                catch (Exception exception)
                {
                    if (IsDisposed())
                    {
                        return;
                    }

                    Log.Write("事件管道异常：" + exception.GetType().Name);
                    SleepAfterError();
                }
                finally
                {
                    lock (_sync)
                    {
                        if (_activeServer == server)
                        {
                            _activeServer = null;
                        }
                    }

                    CloseQuietly(server);
                }
            }
        }

        private NamedPipeServerStream CreateServerPipe()
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

            // 异步模式：配合 ReadAsync + 超时等待，只连接不发送数据的
            // 客户端不会永久占用唯一的管道实例。
            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                security);
        }

        private void HandleClient(NamedPipeServerStream server)
        {
            byte[] buffer = new byte[4096];
            byte[] content;
            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(
                ReadTimeoutMilliseconds);

            using (MemoryStream memory = new MemoryStream())
            {
                while (memory.Length <= EventProtocol.MaxMessageBytes)
                {
                    int read;
                    if (!TryRead(server, buffer, deadlineUtc, out read))
                    {
                        Log.Debug("事件客户端读取超时或已断开");
                        return;
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    int newlineIndex = -1;
                    for (int index = 0; index < read; index++)
                    {
                        if (buffer[index] == (byte)'\n')
                        {
                            newlineIndex = index;
                            break;
                        }
                    }

                    int take = newlineIndex >= 0 ? newlineIndex : read;
                    if (memory.Length + take > EventProtocol.MaxMessageBytes)
                    {
                        Log.Debug("事件消息超过 64KB，已忽略");
                        return;
                    }

                    if (take > 0)
                    {
                        memory.Write(buffer, 0, take);
                    }

                    if (newlineIndex >= 0)
                    {
                        break;
                    }
                }

                content = memory.ToArray();
            }

            if (content.Length == 0)
            {
                return;
            }

            int offset = 0;
            if (content.Length >= 3
                && content[0] == 0xEF
                && content[1] == 0xBB
                && content[2] == 0xBF)
            {
                offset = 3;
            }

            string text = Encoding.UTF8
                .GetString(content, offset, content.Length - offset)
                .Trim();
            if (text.Length == 0)
            {
                return;
            }

            ProcessEnvelope(text);
        }

        /// <summary>
        /// 在连接建立后的总时限内读取一块数据。ReadAsync 让只连接不发送的
        /// 客户端在超时后可被放弃；随后 Run 的 finally 会关闭该管道实例，
        /// 不会阻塞后续连接。
        /// </summary>
        private static bool TryRead(
            NamedPipeServerStream server,
            byte[] buffer,
            DateTime deadlineUtc,
            out int read)
        {
            read = 0;
            double remaining = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0)
            {
                return false;
            }

            int timeout = remaining > int.MaxValue
                ? int.MaxValue
                : (int)remaining;

            Task<int> task;
            try
            {
                task = server.ReadAsync(buffer, 0, buffer.Length);
            }
            catch (Exception exception)
            {
                Log.Debug("事件读取启动失败：" + exception.GetType().Name);
                return false;
            }

            try
            {
                if (!task.Wait(timeout))
                {
                    ObserveFault(task);
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
                    Log.Debug("事件读取异常：" + inner.GetType().Name);
                }

                return false;
            }
            catch (Exception exception)
            {
                Log.Debug("事件读取异常：" + exception.GetType().Name);
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

        private void ProcessEnvelope(string text)
        {
            Dictionary<string, object> envelope;
            try
            {
                envelope = Json.Deserialize<Dictionary<string, object>>(text);
            }
            catch (Exception exception)
            {
                Log.Debug("事件 JSON 解析失败：" + exception.GetType().Name);
                return;
            }

            if (envelope == null)
            {
                Log.Debug("事件 JSON 根节点不是对象，已忽略");
                return;
            }

            int version;
            if (!TryGetVersion(envelope, out version) || version != EventProtocol.ProtocolVersion)
            {
                Log.Debug("事件协议版本不支持，已忽略");
                return;
            }

            object kindValue;
            string kind = null;
            if (envelope.TryGetValue("kind", out kindValue))
            {
                kind = kindValue as string;
            }

            if (!string.Equals(kind, EventProtocol.HookKind, StringComparison.Ordinal))
            {
                Log.Debug(
                    "忽略未知事件类型："
                    + (string.IsNullOrEmpty(kind) ? "空" : kind));
                return;
            }

            object payloadValue;
            Dictionary<string, object> payload = null;
            if (envelope.TryGetValue("payload", out payloadValue))
            {
                payload = payloadValue as Dictionary<string, object>;
            }

            if (payload == null)
            {
                Log.Debug("事件 payload 不是对象，已忽略");
                return;
            }

            try
            {
                _agentProvider.Ingest(payload);
            }
            catch (Exception exception)
            {
                Log.Write("处理 Hook 事件失败：" + exception.GetType().Name);
            }
        }

        private static bool TryGetVersion(
            Dictionary<string, object> envelope,
            out int version)
        {
            version = 0;
            object value;
            if (!envelope.TryGetValue("v", out value) || value == null)
            {
                return false;
            }

            try
            {
                if (value is int)
                {
                    version = (int)value;
                    return true;
                }

                if (value is long)
                {
                    version = checked((int)(long)value);
                    return true;
                }

                if (value is decimal)
                {
                    version = checked((int)(decimal)value);
                    return true;
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            return false;
        }

        private bool IsDisposed()
        {
            lock (_sync)
            {
                return _disposed;
            }
        }

        private static void SleepAfterError()
        {
            try
            {
                Thread.Sleep(ErrorSleepMilliseconds);
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
            NamedPipeServerStream server;

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                server = _activeServer;
                _activeServer = null;
            }

            CloseQuietly(server);

            // 后台线程为 IsBackground，且管道已关闭；即使正在等待一次
            // 读超时，也最多在 2s 后自行退出，不阻塞进程退出。
        }
    }
}
