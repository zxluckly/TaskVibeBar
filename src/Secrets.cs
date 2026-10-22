using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VibeBar
{
    /// <summary>
    /// 凭证存储。整个键值表先用 DPAPI(CurrentUser) 加密，再以 base64 写入 secrets.dat。
    /// 明文只在内存中存在，任何日志都不得包含凭证内容。
    /// </summary>
    internal static class Secrets
    {
        private const string ApiKeyName = "deepseek.apiKey";
        private static readonly object Sync = new object();

        internal static string FilePath
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "VibeBar");
                return Path.Combine(folder, "secrets.dat");
            }
        }

        internal static bool SaveApiKey(string apiKey)
        {
            return Save(ApiKeyName, apiKey);
        }

        internal static string LoadApiKey()
        {
            return Load(ApiKeyName);
        }

        internal static bool DeleteApiKey()
        {
            return Save(ApiKeyName, string.Empty);
        }

        internal static bool Save(string name, string value)
        {
            if (string.IsNullOrEmpty(name) || value == null)
            {
                return false;
            }

            lock (Sync)
            {
                try
                {
                    Dictionary<string, string> values = ReadAllCore();
                    if (value.Length == 0)
                    {
                        values.Remove(name);
                    }
                    else
                    {
                        values[name] = value;
                    }

                    if (values.Count == 0)
                    {
                        if (File.Exists(FilePath))
                        {
                            File.Delete(FilePath);
                        }

                        return true;
                    }

                    string json = Json.Serialize(values);
                    byte[] plain = Encoding.UTF8.GetBytes(json);
                    byte[] encrypted = ProtectedData.Protect(
                        plain,
                        null,
                        DataProtectionScope.CurrentUser);
                    string encoded = Convert.ToBase64String(encrypted);
                    WriteAtomic(encoded);
                    return true;
                }
                catch (Exception exception)
                {
                    Log.Write("保存凭证失败：" + exception.GetType().Name);
                    return false;
                }
            }
        }

        internal static string Load(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            lock (Sync)
            {
                try
                {
                    Dictionary<string, string> values = ReadAllCore();
                    string value;
                    if (values.TryGetValue(name, out value) && !string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
                catch (Exception exception)
                {
                    Log.Write("读取凭证失败，按未配置处理：" + exception.GetType().Name);
                }

                return null;
            }
        }

        private static Dictionary<string, string> ReadAllCore()
        {
            if (!File.Exists(FilePath))
            {
                return new Dictionary<string, string>();
            }

            string encoded = File.ReadAllText(FilePath, Encoding.UTF8).Trim();
            if (encoded.Length == 0)
            {
                return new Dictionary<string, string>();
            }

            byte[] encrypted = Convert.FromBase64String(encoded);
            byte[] plain = ProtectedData.Unprotect(
                encrypted,
                null,
                DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(plain);
            Dictionary<string, string> values =
                Json.Deserialize<Dictionary<string, string>>(json);
            if (values == null)
            {
                return new Dictionary<string, string>();
            }

            return values;
        }

        private static void WriteAtomic(string encoded)
        {
            string folder = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(folder);

            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, encoded, new UTF8Encoding(false));

            try
            {
                if (File.Exists(FilePath))
                {
                    File.Replace(temporary, FilePath, null);
                }
                else
                {
                    File.Move(temporary, FilePath);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
