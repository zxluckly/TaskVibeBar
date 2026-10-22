using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibeBar
{
    /// <summary>
    /// DeepSeek 余额数据源。只查询 /user/balance 并产出 QuotaSnapshot；
    /// 不引用任何 System.Windows.*，不记录 Key、请求头或含 Key 的 URL。
    /// </summary>
    internal sealed class DeepSeekQuotaProvider : IProvider, IDisposable
    {
        internal const string ProviderIdValue = "quota.deepseek";
        private const string DefaultBaseUrl = "https://api.deepseek.com";
        private const int RequestTimeoutMilliseconds = 10000;
        private const int DefaultPollSeconds = 60;
        private const int MinPollSeconds = 60;

        private readonly object _sync = new object();

        private string _baseUrl;
        private bool _readKeyFromClaudeSettings;
        private int _pollSeconds;
        private int _failureCount;
        private QuotaSnapshot _lastSuccess;
        private HttpWebRequest _activeRequest;
        private bool _disposed;

        internal DeepSeekQuotaProvider(
            string baseUrl,
            bool readKeyFromClaudeSettings,
            int pollSeconds)
        {
            _baseUrl = NormalizeBaseUrl(baseUrl);
            _readKeyFromClaudeSettings = readKeyFromClaudeSettings;
            _pollSeconds = ClampPollSeconds(pollSeconds);
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
            get { return TimeSpan.FromSeconds(10); }
        }

        internal void UpdateSettings(
            string baseUrl,
            bool readKeyFromClaudeSettings,
            int pollSeconds)
        {
            lock (_sync)
            {
                _baseUrl = NormalizeBaseUrl(baseUrl);
                _readKeyFromClaudeSettings = readKeyFromClaudeSettings;
                _pollSeconds = ClampPollSeconds(pollSeconds);
            }
        }

        public Task<ProviderResult> QueryAsync(CancellationToken token)
        {
            return QueryCoreAsync(token);
        }

        private async Task<ProviderResult> QueryCoreAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            string key = LoadKey();
            if (string.IsNullOrEmpty(key))
            {
                Log.Debug("DeepSeek 余额查询跳过：未配置 API Key");
                return CreateResult(CreateUnavailableSnapshot());
            }

            string baseUrl;
            lock (_sync)
            {
                baseUrl = _baseUrl;
            }

            string url = baseUrl + "/user/balance";
            DateTime started = DateTime.UtcNow;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.KeepAlive = false;
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;

            AttachRequest(request);
            CancellationTokenRegistration registration = token.Register(
                delegate
                {
                    AbortRequest(request);
                });

            try
            {
                using (WebResponse response = await Task<WebResponse>.Factory.FromAsync(
                    request.BeginGetResponse,
                    request.EndGetResponse,
                    null).ConfigureAwait(false))
                {
                    HttpWebResponse httpResponse = response as HttpWebResponse;
                    int statusCode = httpResponse == null
                        ? 200
                        : (int)httpResponse.StatusCode;

                    string body;
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }

                    QuotaSnapshot parsed = ParseBalanceResponse(body);
                    int elapsed = ElapsedMilliseconds(started);

                    if (parsed.Health == SnapshotHealth.Error)
                    {
                        Log.Write(
                            "DeepSeek 余额查询失败：响应格式异常，HTTP "
                            + statusCode.ToString(CultureInfo.InvariantCulture)
                            + "，"
                            + elapsed.ToString(CultureInfo.InvariantCulture)
                            + "ms");
                        return CreateFailure("响应格式异常");
                    }

                    parsed.UpdatedUtc = DateTime.UtcNow;
                    parsed.Error = string.Empty;

                    lock (_sync)
                    {
                        _failureCount = 0;
                        _lastSuccess = Clone(parsed);
                    }

                    Log.Write(
                        "DeepSeek 余额查询成功：HTTP "
                        + statusCode.ToString(CultureInfo.InvariantCulture)
                        + "，"
                        + elapsed.ToString(CultureInfo.InvariantCulture)
                        + "ms");
                    return CreateResult(parsed);
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    throw;
                }

                Log.Write("DeepSeek 余额查询超时");
                return CreateFailure("请求超时");
            }
            catch (WebException exception)
            {
                if (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }

                HttpWebResponse errorResponse = exception.Response as HttpWebResponse;
                if (errorResponse != null)
                {
                    int statusCode = (int)errorResponse.StatusCode;
                    try
                    {
                        errorResponse.Close();
                    }
                    catch
                    {
                    }

                    int elapsed = ElapsedMilliseconds(started);
                    if (statusCode == 401 || statusCode == 403)
                    {
                        Log.Write(
                            "DeepSeek 余额查询失败：API Key 无效或已过期，HTTP "
                            + statusCode.ToString(CultureInfo.InvariantCulture)
                            + "，"
                            + elapsed.ToString(CultureInfo.InvariantCulture)
                            + "ms");
                        return CreateFailure("API Key 无效或已过期");
                    }

                    Log.Write(
                        "DeepSeek 余额查询失败：HTTP "
                        + statusCode.ToString(CultureInfo.InvariantCulture)
                        + "，"
                        + elapsed.ToString(CultureInfo.InvariantCulture)
                        + "ms");
                    return CreateFailure(
                        "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture));
                }

                if (exception.Status == WebExceptionStatus.Timeout)
                {
                    Log.Write(
                        "DeepSeek 余额查询超时，"
                        + ElapsedMilliseconds(started).ToString(CultureInfo.InvariantCulture)
                        + "ms");
                    return CreateFailure("请求超时");
                }

                Log.Write(
                    "DeepSeek 余额查询失败："
                    + exception.Status.ToString()
                    + "，"
                    + ElapsedMilliseconds(started).ToString(CultureInfo.InvariantCulture)
                    + "ms");
                return CreateFailure("网络请求失败");
            }
            catch (Exception exception)
            {
                if (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }

                Log.Write(
                    "DeepSeek 余额查询异常："
                    + exception.GetType().Name
                    + "，"
                    + ElapsedMilliseconds(started).ToString(CultureInfo.InvariantCulture)
                    + "ms");
                return CreateFailure("网络请求失败");
            }
            finally
            {
                registration.Dispose();
                DetachRequest(request);
            }
        }

        internal static QuotaSnapshot ParseBalanceResponse(string json)
        {
            QuotaSnapshot snapshot = CreateEmptySnapshot();

            try
            {
                if (string.IsNullOrEmpty(json))
                {
                    return FormatError(snapshot);
                }

                Dictionary<string, object> root =
                    Json.Deserialize<Dictionary<string, object>>(json);
                if (root == null)
                {
                    return FormatError(snapshot);
                }

                object availableValue;
                if (root.TryGetValue("is_available", out availableValue)
                    && availableValue is bool)
                {
                    snapshot.IsAvailable = (bool)availableValue;
                }

                object infosValue;
                if (!root.TryGetValue("balance_infos", out infosValue))
                {
                    return FormatError(snapshot);
                }

                IList infos = infosValue as IList;
                if (infos == null || infos.Count == 0)
                {
                    return FormatError(snapshot);
                }

                Dictionary<string, object> first = null;
                Dictionary<string, object> chosen = null;
                for (int index = 0; index < infos.Count; index++)
                {
                    Dictionary<string, object> item =
                        infos[index] as Dictionary<string, object>;
                    if (item == null)
                    {
                        continue;
                    }

                    if (first == null)
                    {
                        first = item;
                    }

                    string currency = GetCurrency(item);
                    if (currency != null
                        && string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = item;
                        break;
                    }
                }

                if (chosen == null)
                {
                    chosen = first;
                }

                if (chosen == null)
                {
                    return FormatError(snapshot);
                }

                string chosenCurrency = GetCurrency(chosen);
                if (string.IsNullOrEmpty(chosenCurrency))
                {
                    return FormatError(snapshot);
                }

                object totalValue;
                if (!chosen.TryGetValue("total_balance", out totalValue)
                    || totalValue == null)
                {
                    return FormatError(snapshot);
                }

                decimal total;
                if (!TryConvertDecimal(totalValue, out total))
                {
                    return FormatError(snapshot);
                }

                decimal granted;
                bool hasGranted = TryGetOptionalDecimal(chosen, "granted_balance", out granted);
                if (!hasGranted && HasValue(chosen, "granted_balance"))
                {
                    return FormatError(snapshot);
                }

                decimal toppedUp;
                bool hasToppedUp = TryGetOptionalDecimal(
                    chosen,
                    "topped_up_balance",
                    out toppedUp);
                if (!hasToppedUp && HasValue(chosen, "topped_up_balance"))
                {
                    return FormatError(snapshot);
                }

                snapshot.Balance = total;
                snapshot.Currency = chosenCurrency;
                snapshot.GrantedBalance = hasGranted ? (decimal?)granted : null;
                snapshot.ToppedUpBalance = hasToppedUp ? (decimal?)toppedUp : null;
                snapshot.Health = SnapshotHealth.Live;
                snapshot.Error = string.Empty;
                snapshot.Source = SnapshotSource.Official;
                snapshot.UpdatedUtc = DateTime.UtcNow;
                return snapshot;
            }
            catch (Exception)
            {
                return FormatError(snapshot);
            }
        }

        internal static string NormalizeBaseUrl(string value)
        {
            string baseUrl = value == null ? string.Empty : value.Trim();
            while (baseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
            }

            if (baseUrl.Length == 0)
            {
                return DefaultBaseUrl;
            }

            int anthropicIndex = baseUrl.IndexOf(
                "/anthropic",
                StringComparison.OrdinalIgnoreCase);
            if (anthropicIndex >= 0)
            {
                Uri uri;
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out uri))
                {
                    baseUrl = uri.Scheme + "://" + uri.Authority;
                }
                else
                {
                    baseUrl = baseUrl.Substring(0, anthropicIndex);
                }

                while (baseUrl.EndsWith("/", StringComparison.Ordinal))
                {
                    baseUrl = baseUrl.Substring(0, baseUrl.Length - 1);
                }
            }

            return baseUrl.Length == 0 ? DefaultBaseUrl : baseUrl;
        }

        private string LoadKey()
        {
            string key = null;
            try
            {
                key = Secrets.LoadApiKey();
            }
            catch (Exception exception)
            {
                Log.Write("读取 API Key 失败：" + exception.GetType().Name);
            }

            if (string.IsNullOrEmpty(key))
            {
                bool readFromClaude;
                lock (_sync)
                {
                    readFromClaude = _readKeyFromClaudeSettings;
                }

                if (readFromClaude)
                {
                    key = ReadClaudeCodeToken();
                }
            }

            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            return key.Trim();
        }

        private static string ReadClaudeCodeToken()
        {
            try
            {
                string profile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(profile))
                {
                    return null;
                }

                string path = Path.Combine(
                    Path.Combine(profile, ".claude"),
                    "settings.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                Dictionary<string, object> root =
                    Json.Deserialize<Dictionary<string, object>>(text);
                if (root == null)
                {
                    return null;
                }

                object envValue;
                if (!root.TryGetValue("env", out envValue))
                {
                    return null;
                }

                Dictionary<string, object> env =
                    envValue as Dictionary<string, object>;
                if (env == null)
                {
                    return null;
                }

                object tokenValue;
                if (!env.TryGetValue("ANTHROPIC_AUTH_TOKEN", out tokenValue))
                {
                    return null;
                }

                string token = tokenValue as string;
                if (string.IsNullOrEmpty(token))
                {
                    return null;
                }

                return token.Trim();
            }
            catch (Exception exception)
            {
                // 只记录异常类型，绝不记录文件内容或 Token。
                Log.Write("读取 Claude Code 配置中的 Key 失败：" + exception.GetType().Name);
                return null;
            }
        }

        private ProviderResult CreateFailure(string error)
        {
            QuotaSnapshot snapshot;
            lock (_sync)
            {
                _failureCount++;
                if (_lastSuccess != null)
                {
                    snapshot = Clone(_lastSuccess);
                    snapshot.Health = _failureCount >= 3
                        ? SnapshotHealth.Stale
                        : SnapshotHealth.Error;
                    snapshot.Error = error;
                }
                else
                {
                    snapshot = CreateEmptySnapshot();
                    snapshot.Health = SnapshotHealth.Error;
                    snapshot.Error = error;
                    snapshot.UpdatedUtc = DateTime.MinValue;
                }
            }

            return CreateResult(snapshot);
        }

        private static QuotaSnapshot CreateUnavailableSnapshot()
        {
            QuotaSnapshot snapshot = CreateEmptySnapshot();
            snapshot.Health = SnapshotHealth.Unavailable;
            snapshot.Error = "未配置 API Key";
            snapshot.UpdatedUtc = DateTime.MinValue;
            return snapshot;
        }

        private static QuotaSnapshot CreateEmptySnapshot()
        {
            QuotaSnapshot snapshot = new QuotaSnapshot();
            snapshot.ProviderId = ProviderIdValue;
            snapshot.DisplayName = "DeepSeek";
            snapshot.Balance = null;
            snapshot.Currency = "CNY";
            snapshot.GrantedBalance = null;
            snapshot.ToppedUpBalance = null;
            snapshot.IsAvailable = false;
            snapshot.IsOffPeak = false;
            snapshot.NextTransitionUtc = DateTime.MinValue;
            snapshot.UpdatedUtc = DateTime.MinValue;
            snapshot.Health = SnapshotHealth.Unavailable;
            snapshot.Source = SnapshotSource.Official;
            snapshot.Error = string.Empty;
            return snapshot;
        }

        private static QuotaSnapshot Clone(QuotaSnapshot source)
        {
            QuotaSnapshot snapshot = new QuotaSnapshot();
            snapshot.ProviderId = source.ProviderId;
            snapshot.DisplayName = source.DisplayName;
            snapshot.Balance = source.Balance;
            snapshot.Currency = source.Currency;
            snapshot.GrantedBalance = source.GrantedBalance;
            snapshot.ToppedUpBalance = source.ToppedUpBalance;
            snapshot.IsAvailable = source.IsAvailable;
            snapshot.IsOffPeak = source.IsOffPeak;
            snapshot.NextTransitionUtc = source.NextTransitionUtc;
            snapshot.UpdatedUtc = source.UpdatedUtc;
            snapshot.Health = source.Health;
            snapshot.Source = source.Source;
            snapshot.Error = source.Error;
            return snapshot;
        }

        private static QuotaSnapshot FormatError(QuotaSnapshot snapshot)
        {
            snapshot.Balance = null;
            snapshot.GrantedBalance = null;
            snapshot.ToppedUpBalance = null;
            snapshot.Health = SnapshotHealth.Error;
            snapshot.Error = "响应格式异常";
            snapshot.UpdatedUtc = DateTime.MinValue;
            Log.Debug("DeepSeek 响应格式异常");
            return snapshot;
        }

        private static string GetCurrency(Dictionary<string, object> item)
        {
            object value;
            if (!item.TryGetValue("currency", out value) || value == null)
            {
                return null;
            }

            return value as string;
        }

        private static bool HasValue(Dictionary<string, object> item, string key)
        {
            object value;
            return item.TryGetValue(key, out value) && value != null;
        }

        private static bool TryGetOptionalDecimal(
            Dictionary<string, object> item,
            string key,
            out decimal value)
        {
            value = 0m;
            object raw;
            if (!item.TryGetValue(key, out raw) || raw == null)
            {
                return false;
            }

            return TryConvertDecimal(raw, out value);
        }

        private static bool TryConvertDecimal(object value, out decimal result)
        {
            result = 0m;
            if (value == null)
            {
                return false;
            }

            try
            {
                string text = value as string;
                if (text != null)
                {
                    return decimal.TryParse(
                        text,
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out result);
                }

                if (value is decimal)
                {
                    result = (decimal)value;
                    return true;
                }

                if (value is int)
                {
                    result = (int)value;
                    return true;
                }

                if (value is long)
                {
                    result = (long)value;
                    return true;
                }

                if (value is double)
                {
                    result = (decimal)(double)value;
                    return true;
                }
            }
            catch (OverflowException)
            {
                return false;
            }

            return false;
        }

        private static ProviderResult CreateResult(QuotaSnapshot snapshot)
        {
            ProviderResult result = new ProviderResult();
            result.ProviderId = ProviderIdValue;
            result.Snapshot = snapshot;
            result.Health = snapshot.Health;
            result.Error = snapshot.Error;
            result.UpdatedUtc = DateTime.UtcNow;
            return result;
        }

        private static int ClampPollSeconds(int seconds)
        {
            if (seconds < MinPollSeconds)
            {
                return DefaultPollSeconds;
            }

            if (seconds > 3600)
            {
                return 3600;
            }

            return seconds;
        }

        private static int ElapsedMilliseconds(DateTime startedUtc)
        {
            double milliseconds = (DateTime.UtcNow - startedUtc).TotalMilliseconds;
            if (milliseconds < 0)
            {
                return 0;
            }

            if (milliseconds > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)milliseconds;
        }

        private void AttachRequest(HttpWebRequest request)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    request.Abort();
                    return;
                }

                _activeRequest = request;
            }
        }

        private void DetachRequest(HttpWebRequest request)
        {
            lock (_sync)
            {
                if (_activeRequest == request)
                {
                    _activeRequest = null;
                }
            }
        }

        private static void AbortRequest(HttpWebRequest request)
        {
            try
            {
                request.Abort();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            HttpWebRequest request;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                request = _activeRequest;
                _activeRequest = null;
            }

            if (request != null)
            {
                AbortRequest(request);
            }
        }
    }
}
