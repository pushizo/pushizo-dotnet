using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HeroSignal.Core;

namespace HeroSignal
{
    /// <summary>
    /// HeroSignal sunucu SDK istemcisi; iş parçacığı güvenlidir ve uygulama boyunca tek örnek olarak kullanılmalıdır.
    /// </summary>
    /// <example>
    /// <code>
    /// using var hs = new HeroSignalClient(new HeroSignalClientOptions { ApiKey = key, AppId = "app_…" });
    /// var result = await hs.Notifications.CreateAsync(new CreateNotificationDto
    /// {
    ///     Target = new CreateNotificationDtoTarget { IncludeAliases = new() { ["external_id"] = new() { "u1" } } },
    ///     Contents = new() { ["en"] = "Shipped" },
    /// }, new RequestOptions { IdempotencyKey = "order-42" });
    /// </code>
    /// </example>
    /// <remarks>
    /// Kaynaklar (<c>Notifications</c>, <c>Users</c>, <c>Segments</c> …) ve modeller <c>api/openapi.yaml</c>'dan üretilir;
    /// HTTP katmanı (kimlik doğrulama, yeniden deneme, idempotency, hata eşleme, sayfalama) el yazımıdır.
    /// </remarks>
    public sealed partial class HeroSignalClient : IDisposable
    {
        /// <summary>SDK sürümü.</summary>
        public const string Version = "0.1.0";

        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3),
        };

        /// <summary>SDK'nın kullandığı JSON ayarları (null alanlar yazılmaz, bilinmeyen alanlar yok sayılır).</summary>
        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly HeroSignalClientOptions _options;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _baseUrl;
        private readonly string _userAgent;

        /// <summary>Yeniden deneme beklemesi (testlerde değiştirilir).</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

        /// <summary>Şimdiki zaman (Retry-After tarihleri için; testlerde değiştirilir).</summary>
        internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

        /// <summary>API anahtarı ve isteğe bağlı varsayılan app ile istemci oluşturur.</summary>
        /// <param name="apiKey"><c>hs_live_…</c> API anahtarı.</param>
        /// <param name="appId">Varsayılan app.</param>
        public HeroSignalClient(string apiKey, string? appId = null)
            : this(new HeroSignalClientOptions { ApiKey = apiKey, AppId = appId })
        {
        }

        /// <summary>Ayarlarla istemci oluşturur.</summary>
        /// <param name="options">İstemci ayarları.</param>
        public HeroSignalClient(HeroSignalClientOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.ApiKey)) throw new ArgumentException("HeroSignal: ApiKey gerekli.", nameof(options));
            _options = options;
            _baseUrl = (options.BaseUrl ?? HeroSignalClientOptions.DefaultBaseUrl).TrimEnd('/');
            _userAgent = string.IsNullOrEmpty(options.UserAgent) ? $"herosignal-dotnet/{Version}" : options.UserAgent!;
            if (options.HttpClient != null)
            {
                _http = options.HttpClient;
            }
            else
            {
                _http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                _ownsHttp = true;
            }
            InitResources();
        }

        /// <summary>İstemcinin varsayılan app kimliği.</summary>
        public string? AppId => _options.AppId;

        /// <summary>SDK identity verification için <c>identity_hash = HMAC-SHA256(sdkSecret, externalId)</c> (hex).</summary>
        /// <param name="sdkSecret">App'in SDK secret'ı.</param>
        /// <param name="externalId">Kullanıcının external_id'si.</param>
        public static string IdentityHash(string sdkSecret, string externalId)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sdkSecret));
            var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(externalId));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        /// <summary>
        /// Düşük seviyeli istek (SDK'da henüz metodu olmayan uçlar için). JSON yanıtlar <see cref="JsonElement"/>,
        /// diğer içerik tipleri (ör. <c>format=csv</c>) JSON dizge değeri olarak döner; boş gövdede null.
        /// </summary>
        /// <param name="method">HTTP metodu.</param>
        /// <param name="path"><c>/v1/…</c> yolu.</param>
        /// <param name="query">Sorgu parametreleri.</param>
        /// <param name="body">JSON gövdesi.</param>
        /// <param name="options">Çağrı seçenekleri.</param>
        /// <param name="cancellationToken">İptal belirteci.</param>
        public Task<JsonElement?> RequestAsync(string method, string path, IEnumerable<KeyValuePair<string, object?>>? query = null, object? body = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            var request = new ApiRequest(method, path) { Body = body };
            if (query != null)
            {
                foreach (var kv in query) request.Query(kv.Key, kv.Value);
            }
            return SendAsync<JsonElement?>(request, options, cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }

        internal async Task SendAsync(ApiRequest request, RequestOptions? options, CancellationToken cancellationToken)
        {
            await SendCoreAsync(request, options, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<T> SendAsync<T>(ApiRequest request, RequestOptions? options, CancellationToken cancellationToken)
        {
            var (bytes, contentType, status, requestId) = await SendCoreAsync(request, options, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0) return default!;
            try
            {
                if ((typeof(T) == typeof(JsonElement?) || typeof(T) == typeof(JsonElement)) && contentType != null && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    var text = JsonSerializer.SerializeToElement(Encoding.UTF8.GetString(bytes));
                    return (T)(object)text;
                }
                return JsonSerializer.Deserialize<T>(bytes, JsonOptions)!;
            }
            catch (JsonException e)
            {
                throw new HeroSignalException(status, "INVALID_RESPONSE", $"yanıt çözülemedi: {e.Message}", requestId, null, e);
            }
        }

        private string ResolveAppId(RequestOptions? options)
        {
            var id = !string.IsNullOrEmpty(options?.AppId) ? options!.AppId : _options.AppId;
            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidOperationException("HeroSignal: app ID verilmedi (HeroSignalClientOptions.AppId veya RequestOptions.AppId).");
            }
            return id!;
        }

        private async Task<(byte[] Body, string? ContentType, int Status, string? RequestId)> SendCoreAsync(ApiRequest request, RequestOptions? options, CancellationToken cancellationToken)
        {
            var path = request.Path;
            var query = new StringBuilder();
            string? appId = null;
            if (request.AppIdInPath || request.QueryAppId || request.BodyAppId)
            {
                appId = ResolveAppId(options);
                if (request.AppIdInPath) path = path.Replace("{appId}", Uri.EscapeDataString(appId), StringComparison.Ordinal);
                if (request.QueryAppId) AppendQuery(query, "app_id", appId);
            }
            foreach (var p in request.PathParams)
            {
                path = path.Replace("{" + p.Key + "}", Uri.EscapeDataString(p.Value), StringComparison.Ordinal);
            }
            foreach (var q in request.QueryParams)
            {
                foreach (var v in ScalarValues(q.Value)) AppendQuery(query, q.Key, v);
            }

            var idempotencyKey = options?.IdempotencyKey;
            byte[]? json = null;
            if (!request.Multipart && request.Body != null)
            {
                var node = JsonSerializer.SerializeToNode(request.Body, request.Body.GetType(), JsonOptions);
                if (node is JsonObject obj)
                {
                    if (request.BodyAppId)
                    {
                        var existing = obj["app_id"];
                        if (existing == null || (existing is JsonValue ev && ev.TryGetValue<string>(out var s) && string.IsNullOrEmpty(s)))
                        {
                            obj["app_id"] = appId;
                        }
                    }
                    if (string.IsNullOrEmpty(idempotencyKey) && obj["idempotency_key"] is JsonValue kv && kv.TryGetValue<string>(out var bodyKey))
                    {
                        idempotencyKey = bodyKey;
                    }
                }
                json = Encoding.UTF8.GetBytes(node?.ToJsonString(JsonOptions) ?? "null");
            }
            if (string.IsNullOrEmpty(idempotencyKey) && request.Idempotent && _options.AutoIdempotencyKey)
            {
                idempotencyKey = Guid.NewGuid().ToString();
            }

            var uri = new Uri(_baseUrl + path + (query.Length > 0 ? "?" + query : string.Empty));
            var maxRetries = options?.MaxRetries ?? _options.MaxRetries;
            var timeout = options?.Timeout ?? _options.Timeout;

            for (var attempt = 0; ; attempt++)
            {
                using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
                message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.ApiKey);
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                message.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                if (!string.IsNullOrEmpty(idempotencyKey)) message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
                if (options != null)
                {
                    foreach (var h in options.Headers)
                    {
                        message.Headers.Remove(h.Key);
                        message.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                }
                if (request.Multipart)
                {
                    message.Content = BuildMultipart(request);
                }
                else if (json != null)
                {
                    message.Content = new ByteArrayContent(json);
                    message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }

                HeroSignalException error;
                HttpResponseMessage? response = null;
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (timeout > TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan) attemptCts.CancelAfter(timeout);
                try
                {
                    response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, attemptCts.Token).ConfigureAwait(false);
                    var bytes = await response.Content.ReadAsByteArrayAsync(attemptCts.Token).ConfigureAwait(false);
                    var status = (int)response.StatusCode;
                    var headerRequestId = response.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
                    if (status >= 200 && status < 300)
                    {
                        var contentType = response.Content.Headers.ContentType?.MediaType;
                        response.Dispose();
                        return (bytes, contentType, status, headerRequestId);
                    }
                    error = ErrorFrom(status, bytes, headerRequestId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    response?.Dispose();
                    throw;
                }
                catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException || e is System.IO.IOException)
                {
                    response?.Dispose();
                    response = null;
                    var msg = e is OperationCanceledException ? $"istek zaman aşımına uğradı ({timeout.TotalSeconds:0.###} sn)" : e.Message;
                    error = new HeroSignalException(0, "NETWORK", msg, null, null, e);
                }

                if (!error.IsRetryable || attempt >= maxRetries)
                {
                    response?.Dispose();
                    throw error;
                }
                var delay = RetryDelay(response, attempt);
                response?.Dispose();
                await Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        private static HeroSignalException ErrorFrom(int status, byte[] body, string? headerRequestId)
        {
            string? code = null, message = null, requestId = null;
            Dictionary<string, JsonElement>? details = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    code = Text(err, "code");
                    message = Text(err, "message");
                    requestId = Text(err, "request_id");
                    if (err.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
                    {
                        details = new Dictionary<string, JsonElement>();
                        foreach (var p in d.EnumerateObject()) details[p.Name] = p.Value.Clone();
                    }
                }
            }
            catch (JsonException)
            {
                // Gövde JSON değilse durum kodundan türetilen hata kullanılır.
            }
            return new HeroSignalException(
                status,
                string.IsNullOrEmpty(code) ? $"HTTP_{status}" : code!,
                string.IsNullOrEmpty(message) ? $"HTTP {status}" : message!,
                string.IsNullOrEmpty(requestId) ? headerRequestId : requestId,
                details);
        }

        private static string? Text(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>Retry-After (saniye veya HTTP tarihi) varsa ona uyar; yoksa 300 ms / 1 sn / 3 sn.</summary>
        private TimeSpan RetryDelay(HttpResponseMessage? response, int attempt)
        {
            var retryAfter = response?.Headers.RetryAfter;
            if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero) return delta;
            if (retryAfter?.Date is DateTimeOffset date)
            {
                var d = date - Clock();
                if (d > TimeSpan.Zero) return d;
            }
            if (response != null && response.Headers.TryGetValues("Retry-After", out var raw)
                && double.TryParse(raw.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs > 0)
            {
                return TimeSpan.FromSeconds(secs);
            }
            return Backoff[Math.Min(attempt, Backoff.Length - 1)];
        }

        /// <summary>Sorgu/form değeri: koleksiyonlar tekrarlanan anahtar, bool küçük harf, karmaşık değerler JSON.</summary>
        internal static IEnumerable<string> ScalarValues(object? value)
        {
            switch (value)
            {
                case null:
                    yield break;
                case string s:
                    yield return s;
                    yield break;
                case bool b:
                    yield return b ? "true" : "false";
                    yield break;
                case IFormattable f when value is not IEnumerable:
                    yield return f.ToString(null, CultureInfo.InvariantCulture);
                    yield break;
                case IEnumerable e when value is not IDictionary:
                    foreach (var item in e)
                    {
                        foreach (var v in ScalarValues(item)) yield return v;
                    }
                    yield break;
                default:
                    yield return JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
                    yield break;
            }
        }

        private static void AppendQuery(StringBuilder sb, string key, string value)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        private static MultipartFormDataContent BuildMultipart(ApiRequest request)
        {
            var content = new MultipartFormDataContent();
            foreach (var f in request.FormFields)
            {
                foreach (var v in ScalarValues(f.Value)) content.Add(new StringContent(v, Encoding.UTF8), f.Key);
            }
            foreach (var f in request.FormFiles)
            {
                var part = new ByteArrayContent(f.Value.GetContent());
                part.Headers.ContentType = MediaTypeHeaderValue.Parse(f.Value.ContentType);
                content.Add(part, f.Key, f.Value.FileName);
            }
            return content;
        }
    }
}
