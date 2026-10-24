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

            SendToPipe(payload, EventProtocol.StatusLineKind);
            PassThroughOriginalStatusLine(payload);
            return 0;
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
            catch
            {
                // 事件桥失败不能影响 Claude Code；statusLine 仍会走原命令透传。
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
