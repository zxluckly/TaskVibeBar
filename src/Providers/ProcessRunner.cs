using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// 外部命令执行结果。Provider 层共用：Git、gh、自定义命令都走这里，
    /// 保证超时、取消、编码与进程回收的行为一致。
    /// </summary>
    internal sealed class ProcessRunResult
    {
        internal string Stdout = string.Empty;
        internal string Stderr = string.Empty;
        internal int ExitCode;
        internal bool TimedOut;
        internal bool NotFound;
        internal bool Cancelled;
    }

    internal static class ProcessRunner
    {
        /// <summary>
        /// 运行外部命令，超时/取消都会杀掉进程树，绝不阻塞调用方线程。
        /// 不引用 System.Windows.*，可在 Provider 后台线程直接调用。
        /// </summary>
        internal static async Task<ProcessRunResult> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            int timeoutMilliseconds,
            Encoding outputEncoding,
            IDictionary<string, string> extraEnvironment,
            CancellationToken token)
        {
            ProcessRunResult result = new ProcessRunResult();
            if (string.IsNullOrEmpty(fileName))
            {
                result.NotFound = true;
                return result;
            }

            if (timeoutMilliseconds <= 0)
            {
                timeoutMilliseconds = 5000;
            }

            Process process = new Process();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments == null ? string.Empty : arguments;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            Encoding encoding = outputEncoding == null
                ? new UTF8Encoding(false)
                : outputEncoding;
            startInfo.StandardOutputEncoding = encoding;
            startInfo.StandardErrorEncoding = encoding;

            ApplySanitizedEnvironment(startInfo, extraEnvironment);

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
                        result.NotFound = true;
                        return result;
                    }
                }
                catch (Win32Exception)
                {
                    result.NotFound = true;
                    return result;
                }
                catch (InvalidOperationException)
                {
                    result.NotFound = true;
                    return result;
                }

                process.EnableRaisingEvents = true;
                process.Exited += delegate { exitSource.TrySetResult(true); };
                if (process.HasExited)
                {
                    exitSource.TrySetResult(true);
                }

                stdoutTask = process.StandardOutput.ReadToEndAsync();
                stderrTask = process.StandardError.ReadToEndAsync();

                registration = token.Register(delegate { TryKill(process); });

                Task completed = await Task.WhenAny(
                    exitSource.Task,
                    Task.Delay(timeoutMilliseconds)).ConfigureAwait(false);

                if (completed != exitSource.Task)
                {
                    result.TimedOut = true;
                    TryKill(process);
                    try
                    {
                        process.WaitForExit(500);
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
                await Task.WhenAny(allOutput, Task.Delay(1000)).ConfigureAwait(false);
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
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 启动时清理进程环境里「仅大小写不同」的重复变量。
        /// .NET Framework 的 ProcessStartInfo.EnvironmentVariables 用大小写不敏感的
        /// StringDictionary 复制父进程环境，遇到 HTTPS_PROXY / https_proxy 同时存在
        /// （代理工具常见写法）会直接抛 ArgumentException，导致所有子进程无法启动。
        /// 这里保留首个出现的键，移除其余重复项。
        /// </summary>
        internal static void SanitizeProcessEnvironment()
        {
            try
            {
                IDictionary variables = Environment.GetEnvironmentVariables();
                Dictionary<string, string> seen =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<string> duplicates = new List<string>();

                foreach (DictionaryEntry entry in variables)
                {
                    string key = entry.Key as string;
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    if (seen.ContainsKey(key))
                    {
                        duplicates.Add(key);
                    }
                    else
                    {
                        seen[key] = entry.Value as string;
                    }
                }

                for (int index = 0; index < duplicates.Count; index++)
                {
                    Environment.SetEnvironmentVariable(duplicates[index], null);
                    Log.Write(
                        "环境变量存在仅大小写不同的重复键，已移除："
                        + duplicates[index]);
                }
            }
            catch (Exception exception)
            {
                Log.Write("清理重复环境变量失败：" + exception.GetType().Name);
            }
        }

        /// <summary>
        /// 合并额外环境变量。父进程环境已在启动时去重，这里不再重建整块环境，
        /// 避免再次触发 .NET Framework 的 StringDictionary 重复键缺陷。
        /// </summary>
        private static void ApplySanitizedEnvironment(
            ProcessStartInfo startInfo,
            IDictionary<string, string> extraEnvironment)
        {
            if (extraEnvironment == null || extraEnvironment.Count == 0)
            {
                return;
            }

            try
            {
                foreach (KeyValuePair<string, string> pair in extraEnvironment)
                {
                    startInfo.EnvironmentVariables[pair.Key] = pair.Value;
                }
            }
            catch (Exception exception)
            {
                // 环境块处理失败不阻断执行：退回默认行为，由调用方按错误展示。
                Log.Debug("准备子进程环境变量失败：" + exception.GetType().Name);
            }
        }

        /// <summary>
        /// 控制台命令（cmd /c）使用的编码：中文系统为 GBK(936)，
        /// 直接用 GetOEMCP 取当前控制台代码页，避免中文输出乱码。
        /// </summary>
        internal static Encoding ConsoleEncoding()
        {
            try
            {
                int codePage = NativeMethods.GetOEMCP();
                if (codePage > 0)
                {
                    return Encoding.GetEncoding(codePage);
                }
            }
            catch (Exception exception)
            {
                Log.Debug("获取控制台代码页失败：" + exception.GetType().Name);
            }

            return new UTF8Encoding(false);
        }

        internal static void TryKill(Process process)
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
    }
}
