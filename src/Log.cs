using System;
using System.IO;
using System.Text;

namespace VibeBar
{
    internal static class Log
    {
        private const long MaxBytes = 1024L * 1024L;
        private static readonly object Sync = new object();

        internal static string FilePath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VibeBar");
                return Path.Combine(folder, "log.txt");
            }
        }

        internal static void Write(string message)
        {
            Append("INFO", message);
        }

        internal static void Debug(string message)
        {
            Append("DEBUG", message);
        }

        internal static void Error(string message, Exception exception)
        {
            string text = message;
            if (exception != null)
            {
                text = text + "：" + exception.GetType().Name + " " + exception.Message;
            }

            Append("ERROR", text);
        }

        internal static void Exception(string message, Exception exception)
        {
            string text = message;
            if (exception != null)
            {
                text = text + "：" + exception;
            }

            Append("ERROR", text);
        }

        private static void Append(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    string path = FilePath;
                    string folder = Path.GetDirectoryName(path);
                    if (!Directory.Exists(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    RotateIfNeeded(path);

                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + " [" + level + "] "
                        + Redact(message == null ? string.Empty : message)
                        + Environment.NewLine;

                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志失败不能影响主流程。
            }
        }

        private static void RotateIfNeeded(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            string oldPath = path + ".old";
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            File.Move(path, oldPath);
        }

        internal static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)
                || text.IndexOf("sk-", StringComparison.Ordinal) < 0)
            {
                return text;
            }

            StringBuilder builder = new StringBuilder(text.Length + 8);
            int cursor = 0;
            while (cursor < text.Length)
            {
                int index = text.IndexOf("sk-", cursor, StringComparison.Ordinal);
                if (index < 0)
                {
                    builder.Append(text, cursor, text.Length - cursor);
                    break;
                }

                builder.Append(text, cursor, index - cursor);
                builder.Append("sk-***");

                int end = index + 3;
                while (end < text.Length && IsSecretChar(text[end]))
                {
                    end++;
                }

                cursor = end;
            }

            return builder.ToString();
        }

        private static bool IsSecretChar(char value)
        {
            return char.IsLetterOrDigit(value) || value == '-' || value == '_';
        }
    }
}
