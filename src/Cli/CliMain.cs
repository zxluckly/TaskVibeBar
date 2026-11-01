using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// VibeBar.Cli.exe 入口。供 Claude Code Hooks 调用：
    /// 读取 stdin 一行 JSON，包成 IPC 信封后写入命名管道。
    /// 程序未运行、管道不可用或写入失败时静默退出 0，绝不向 stdout/stderr 输出。
    /// </summary>
    internal static class CliMain
    {
        private const string HookArgument = "--hook";
        private const string StatusLineArgument = "--statusline";
        private const int ConnectTimeoutMilliseconds = 250;
        private const int ErrorFileNotFound = 2;
        private const int OriginalStatusLineTimeoutMilliseconds = 2000;

        // 外部段（--segment*）与 Hook 路径完全独立：管道名与 SegmentProtocol 保持一致，
        // 但 CLI 只引用 System/System.Core，不能编译 SegmentProtocol.cs（它依赖 Json.cs）。
        private const string SegmentPipeName = "VibeBar.Segment.v1";
        private const int SegmentTimeoutMilliseconds = 1200;
        private const int SegmentExitOk = 0;
        private const int SegmentExitUsage = 2;
        private const int SegmentExitUnavailable = 3;
        private const int SegmentExitRejected = 4;
        private const string SegmentUnavailableMessage = "VibeBar 未运行或未启用外部段";

        private static readonly string SegmentUsageText =
            "用法：\r\n"
            + "  VibeBar.Cli.exe --segment id=<id> [text=<文本>] [label=<前缀>] [tooltip=<提示>]\r\n"
            + "                  [color=<neutral|accent|warning|critical|success|waiting>]\r\n"
            + "                  [url=<链接>] [priority=<0-1000>] [ttl=<5-3600>] [detail=<行1|行2>]\r\n"
            + "                  [--verbose]\r\n"
            + "  VibeBar.Cli.exe --segment-remove id=<id> [--verbose]\r\n"
            + "  VibeBar.Cli.exe --segment-clear id=<前缀> [--verbose]\r\n";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipe(string lpNamedPipeName, int nTimeOut);

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch
            {
                // Hook 调用路径必须绝对安静：任何异常都退出 0。
                return 0;
            }
        }

        private static int Run(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return 0;
            }

            // --segment* 必须在 hook/statusline 判断之前路由，且自己返回 2/3/4，
            // 不能落到 Main 外层的 catch { return 0; }，否则脚本会误判成功。
            if (SegmentCliArgs.IsSegmentArgument(args))
            {
                return RunSegment(args);
            }

            bool isHook = string.Equals(
                args[0],
                HookArgument,
                StringComparison.OrdinalIgnoreCase);
            bool isStatusLine = string.Equals(
                args[0],
                StatusLineArgument,
                StringComparison.OrdinalIgnoreCase);

            if (!isHook && !isStatusLine)
            {
                return 0;
            }

            string payload = ReadStandardInput();
            if (string.IsNullOrEmpty(payload))
            {
                return 0;
            }

            payload = payload.Trim();
            if (payload.Length == 0)
            {
                return 0;
            }

            if (isHook)
            {
                SendToPipe(payload, EventProtocol.HookKind);
                return 0;
            }

            Diag("statusline invoked len=" + payload.Length);
            SendToPipe(payload, EventProtocol.StatusLineKind);
            Diag("statusline sent");
            PassThroughOriginalStatusLine(payload);
            return 0;
        }

        /// <summary>
        /// --segment / --segment-remove / --segment-clear：推一帧到 VibeBar.Segment.v1。
        /// 成功静默返回 0；参数错误 2；管道不可用 3；服务端拒绝或回复超时 4。
        /// 异常在本方法内消化，不依赖 Main 外层的 catch（那会返回 0）。
        /// </summary>
        private static int RunSegment(string[] args)
        {
            try
            {
                SegmentCliArgs parsed;
                if (!SegmentCliArgs.TryParse(args, out parsed) || parsed == null)
                {
                    return SegmentExitOk;
                }

                if (!string.IsNullOrEmpty(parsed.Error))
                {
                    WriteSegmentText("参数错误：" + parsed.Error, true);
                    WriteSegmentText(SegmentUsageText, true);
                    return SegmentExitUsage;
                }

                return SendSegmentFrame(parsed);
            }
            catch (Exception exception)
            {
                WriteSegmentText("发送失败：" + exception.GetType().Name, true);
                return SegmentExitRejected;
            }
        }

        private static int SendSegmentFrame(SegmentCliArgs parsed)
        {
            string frame = parsed.BuildFrameJson();
            byte[] bytes = new UTF8Encoding(false).GetBytes(frame + "\n");
            string reply;

            NamedPipeClientStream client = null;
            try
            {
                client = new NamedPipeClientStream(
                    ".",
                    SegmentPipeName,
                    PipeDirection.InOut,
                    PipeOptions.None);

                try
                {
                    client.Connect(SegmentTimeoutMilliseconds);
                }
                catch (TimeoutException)
                {
                    WriteSegmentText(SegmentUnavailableMessage, true);
                    return SegmentExitUnavailable;
                }
                catch (IOException)
                {
                    WriteSegmentText(SegmentUnavailableMessage, true);
                    return SegmentExitUnavailable;
                }

                client.Write(bytes, 0, bytes.Length);
                client.Flush();

                try
                {
                    reply = ReadSegmentReply(client);
                }
                catch (TimeoutException)
                {
                    WriteSegmentText("等待服务端回复超时（timeout）", true);
                    return SegmentExitRejected;
                }
            }
            catch (IOException)
            {
                WriteSegmentText(SegmentUnavailableMessage, true);
                return SegmentExitUnavailable;
            }
            finally
            {
                if (client != null)
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch
                    {
                    }
                }
            }

            if (parsed.Verbose && !string.IsNullOrEmpty(reply))
            {
                WriteSegmentText(reply, false);
            }

            if (string.IsNullOrEmpty(reply))
            {
                WriteSegmentText("服务端未返回回复（empty_reply）", true);
                return SegmentExitRejected;
            }

            if (reply.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0)
            {
                return SegmentExitOk;
            }

            string error = ExtractReplyError(reply);
            WriteSegmentText(
                string.IsNullOrEmpty(error) ? "服务端拒绝：invalid_reply" : error,
                true);
            return SegmentExitRejected;
        }

        /// <summary>
        /// 读一行回复，最多等 SegmentTimeoutMilliseconds；超时抛 TimeoutException。
        /// </summary>
        private static string ReadSegmentReply(Stream stream)
        {
            byte[] buffer = new byte[512];
            int total = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(SegmentTimeoutMilliseconds);

            while (total < buffer.Length)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                {
                    throw new TimeoutException();
                }

                Task<int> readTask = stream.ReadAsync(buffer, total, buffer.Length - total);
                try
                {
                    if (!readTask.Wait(remaining))
                    {
                        throw new TimeoutException();
                    }
                }
                catch (AggregateException exception)
                {
                    throw exception.GetBaseException();
                }

                int read = readTask.Result;
                if (read <= 0)
                {
                    break;
                }

                total += read;
                if (buffer[total - 1] == (byte)'\n')
                {
                    break;
                }
            }

            if (total <= 0)
            {
                return null;
            }

            string text = Encoding.UTF8.GetString(buffer, 0, total).Trim();
            return text.Length == 0 ? null : text;
        }

        private static string ExtractReplyError(string reply)
        {
            const string marker = "\"error\":\"";
            int start = reply.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            int end = reply.IndexOf('"', start);
            if (end <= start)
            {
                return null;
            }

            return reply.Substring(start, end - start);
        }

        /// <summary>
        /// 写一行到 stdout/stderr，沿用控制台代码页（Windows 脚本捕获时不会乱码）。
        /// 写失败（管道已关闭等）时忽略，不能让输出问题影响退出码。
        /// </summary>
        private static void WriteSegmentText(string message, bool toError)
        {
            if (message == null)
            {
                return;
            }

            try
            {
                if (toError)
                {
                    Console.Error.WriteLine(message);
                    Console.Error.Flush();
                }
                else
                {
                    Console.Out.WriteLine(message);
                    Console.Out.Flush();
                }
            }
            catch
            {
            }
        }

        private static void SendToPipe(string payload, string kind)
        {
            try
            {
                string envelope = EventProtocol.CreateEnvelope(kind, payload);
                byte[] bytes = new UTF8Encoding(false).GetBytes(envelope + "\n");

                // 快速失败：程序未运行时 WaitNamedPipe(...,0) 立即返回
                // ERROR_FILE_NOT_FOUND，避免 Connect(250) 固定等待 250ms。
                string fullPipeName = @"\\.\pipe\" + EventProtocol.PipeName;
                if (!WaitNamedPipe(fullPipeName, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorFileNotFound)
                    {
                        Diag("pipe not found");
                        return;
                    }
                }

                using (NamedPipeClientStream client = new NamedPipeClientStream(
                    ".",
                    EventProtocol.PipeName,
                    PipeDirection.Out,
                    PipeOptions.None))
                {
                    client.Connect(ConnectTimeoutMilliseconds);

                    try
                    {
                        client.WriteTimeout = ConnectTimeoutMilliseconds;
                    }
                    catch
                    {
                        // 某些平台不支持 PipeStream 写超时；本地管道写入通常瞬时完成。
                    }

                    client.Write(bytes, 0, bytes.Length);
                    client.Flush();
                }
            }
            catch (Exception exception)
            {
                // 事件桥失败不能影响 Claude Code；statusLine 仍会走原命令透传。
                Diag("send error " + exception.GetType().Name);
            }
        }

        private static void Diag(string message)
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VibeBar");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "statusline-diag.log");
                FileInfo info = new FileInfo(path);
                if (info.Exists && info.Length > 128 * 1024)
                {
                    return;
                }

                File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + " "
                        + message
                        + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        /// <summary>
        /// 若用户原本配置了 statusLine，则把同一份 JSON 转发给原命令并原样输出；
        /// 没有原命令时输出空，保持终端状态栏干净。
        /// </summary>
        private static void PassThroughOriginalStatusLine(string payload)
        {
            string command = ReadOriginalStatusLineCommand();
            if (string.IsNullOrEmpty(command))
            {
                return;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(
                    "cmd.exe",
                    "/d /s /c \"" + command + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardInput = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.StandardOutputEncoding = new UTF8Encoding(false);
                info.StandardErrorEncoding = new UTF8Encoding(false);

                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        return;
                    }

                    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = process.StandardError.ReadToEndAsync();

                    process.StandardInput.Write(payload);
                    process.StandardInput.Close();

                    if (!process.WaitForExit(OriginalStatusLineTimeoutMilliseconds))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        return;
                    }

                    string output = stdout.Result;
                    if (!string.IsNullOrEmpty(output))
                    {
                        Console.Out.Write(output);
                        Console.Out.Flush();
                    }
                }
            }
            catch
            {
                // 原 statusLine 失败时保持安静，不污染 Claude Code 输出。
            }
        }

        private static string ReadOriginalStatusLineCommand()
        {
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VibeBar");
                string path = Path.Combine(folder, "statusline-original.txt");
                if (!File.Exists(path))
                {
                    return null;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                return string.IsNullOrEmpty(text) ? null : text.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string ReadStandardInput()
        {
            byte[] buffer = new byte[4096];
            byte[] content;
            int total = 0;

            using (Stream standardInput = Console.OpenStandardInput())
            using (MemoryStream memory = new MemoryStream())
            {
                while (total < EventProtocol.MaxMessageBytes)
                {
                    int remaining = EventProtocol.MaxMessageBytes - total;
                    int request = remaining < buffer.Length ? remaining : buffer.Length;
                    int read = standardInput.Read(buffer, 0, request);
                    if (read <= 0)
                    {
                        break;
                    }

                    memory.Write(buffer, 0, read);
                    total += read;
                }

                if (total <= 0)
                {
                    return null;
                }

                content = memory.ToArray();
            }

            // 去掉可能存在的 UTF-8 BOM。
            int offset = 0;
            if (content.Length >= 3
                && content[0] == 0xEF
                && content[1] == 0xBB
                && content[2] == 0xBF)
            {
                offset = 3;
            }

            return Encoding.UTF8.GetString(content, offset, content.Length - offset);
        }
    }
}
