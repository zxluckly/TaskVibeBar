using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VibeBar
{
    /// <summary>
    /// 本机监听端口的枚举（IP Helper API）。不引用 System.Windows.*；
    /// 只做同步的轻量查询，调用方负责放到后台线程。
    /// </summary>
    internal static class PortScanner
    {
        private const int RowSize = 24;          // MIB_TCPROW_OWNER_PID
        private const int OffsetState = 0;
        private const int OffsetLocalPort = 8;
        private const int OffsetOwningPid = 20;
        private const int StateListen = 2;       // MIB_TCP_STATE_LISTEN
        private const int MaxBufferBytes = 8 * 1024 * 1024;

        private static readonly object NameSync = new object();
        private static readonly Dictionary<int, string> NameCache =
            new Dictionary<int, string>();

        /// <summary>
        /// 返回当前所有处于 LISTEN 状态的 TCP 端口（按端口号升序）。
        /// 失败时返回空列表，不抛异常（由调用方决定错误展示）。
        /// </summary>
        internal static List<PortEntry> GetListeningPorts()
        {
            List<PortEntry> entries = new List<PortEntry>();

            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                uint code = NativeMethods.GetExtendedTcpTable(
                    IntPtr.Zero,
                    ref size,
                    false,
                    NativeMethods.AF_INET,
                    NativeMethods.TCP_TABLE_OWNER_PID_LISTENER,
                    0);

                if (size <= 0 || size > MaxBufferBytes)
                {
                    Log.Debug("端口枚举失败：缓冲区大小异常");
                    return entries;
                }

                buffer = Marshal.AllocHGlobal(size);
                code = NativeMethods.GetExtendedTcpTable(
                    buffer,
                    ref size,
                    false,
                    NativeMethods.AF_INET,
                    NativeMethods.TCP_TABLE_OWNER_PID_LISTENER,
                    0);

                if (code != 0)
                {
                    Log.Debug("端口枚举失败：GetExtendedTcpTable 返回 " + code);
                    return entries;
                }

                int count = Marshal.ReadInt32(buffer);
                long rowAddress = buffer.ToInt64() + 4;
                for (int index = 0; index < count; index++)
                {
                    IntPtr row = new IntPtr(rowAddress + (long)index * RowSize);
                    int state = Marshal.ReadInt32(row, OffsetState);
                    if (state != StateListen)
                    {
                        continue;
                    }

                    int rawPort = Marshal.ReadInt32(row, OffsetLocalPort);
                    int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                    if (port <= 0)
                    {
                        continue;
                    }

                    int processId = Marshal.ReadInt32(row, OffsetOwningPid);
                    PortEntry entry = new PortEntry();
                    entry.Port = port;
                    entry.ProcessId = processId;
                    entry.ProcessName = ResolveProcessName(processId);
                    entries.Add(entry);
                }
            }
            catch (Exception exception)
            {
                Log.Write("端口枚举异常：" + exception.GetType().Name);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            entries.Sort(delegate(PortEntry left, PortEntry right)
            {
                return left.Port.CompareTo(right.Port);
            });

            return entries;
        }

        private static string ResolveProcessName(int processId)
        {
            if (processId <= 0)
            {
                return string.Empty;
            }

            lock (NameSync)
            {
                string cached;
                if (NameCache.TryGetValue(processId, out cached))
                {
                    return cached;
                }
            }

            string name = string.Empty;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    name = process.ProcessName;
                }
            }
            catch (ArgumentException)
            {
                // 进程已退出。
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception exception)
            {
                Log.Debug("读取进程名失败：" + exception.GetType().Name);
            }

            lock (NameSync)
            {
                // 缓存上限，避免长期运行后 PID 复用导致缓存膨胀。
                if (NameCache.Count > 512)
                {
                    NameCache.Clear();
                }

                NameCache[processId] = name;
            }

            return name;
        }

        /// <summary>
        /// 把端口过滤为「关注列表」；watchPorts 为空表示不过滤（全部监听端口）。
        /// </summary>
        internal static List<PortEntry> Filter(List<PortEntry> all, int[] watchPorts)
        {
            if (all == null)
            {
                return new List<PortEntry>();
            }

            if (watchPorts == null || watchPorts.Length == 0)
            {
                return all;
            }

            List<PortEntry> result = new List<PortEntry>();
            for (int index = 0; index < all.Count; index++)
            {
                PortEntry entry = all[index];
                if (entry == null)
                {
                    continue;
                }

                for (int watchIndex = 0; watchIndex < watchPorts.Length; watchIndex++)
                {
                    if (watchPorts[watchIndex] == entry.Port)
                    {
                        result.Add(entry);
                        break;
                    }
                }
            }

            return result;
        }
    }
}
