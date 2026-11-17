using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Pushizo.Core;

namespace Pushizo
{
    /// <summary>
    /// Pushizo .NET sunucu SDK istemcisi (HttpClient tabanlı, eşzamanlı kullanım için güvenli).
    /// <code>
    /// using var hs = new PushizoClient(new PushizoOptions { ApiKey = Environment.GetEnvironmentVariable("HS_API_KEY")!, AppId = "app_…" });
    /// var res = await hs.Notifications.CreateAsync(new CreateNotificationDto
    /// {
    ///     Target = new CreateNotificationDtoTarget { IncludeAliases = new() { ["external_id"] = new() { userId } } },
    ///     Contents = new() { ["en"] = "Shipped", ["tr"] = "Kargoya verildi" },
    /// }, new RequestOptions { IdempotencyKey = $"order-{orderId}" });
    /// </code>
    /// Kaynak özellikleri (<c>Notifications</c>, <c>Users</c>, <c>Segments</c> …) <c>api/openapi.yaml</c>'dan üretilir.
    /// </summary>
    public sealed partial class PushizoClient : IDisposable
    {
        /// <summary>SDK sürümü (User-Agent başlığında gönderilir).</summary>
        public const string Version = "0.3.0";

        /// <summary>Varsayılan API adresi.</summary>
        public const string DefaultBaseUrl = "https://api.pushizo.com";

        private static readonly TimeSpan[] Backoff = { TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };

        /// <summary>Sunucuda tekrarlanması ek yan etki doğurmayan HTTP metotları; 5xx ve ağ hatalarında serbestçe yeniden denenir.</summary>
        private static readonly HashSet<string> IdempotentMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "PUT", "DELETE" };

        private readonly PushizoOptions _options;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _baseUrl;
        private readonly string _userAgent;

        /// <summary>Yeniden deneme beklemesi (testlerde değiştirilir).</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

        public PushizoClient(PushizoOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.ApiKey))
            {
                throw new ArgumentException("Pushizo: ApiKey gerekli.", nameof(options));
            }
            _baseUrl = (options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/');
            _userAgent = options.UserAgent ?? $"pushizo-dotnet/{Version}";
            _ownsHttp = options.HttpClient is null;
            _http = options.HttpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            InitResources();
        }

        /// <summary>Yalnızca API anahtarıyla istemci oluşturur.</summary>
        public PushizoClient(string apiKey, string? appId = null)
            : this(new PushizoOptions { ApiKey = apiKey, AppId = appId })
        {
        }

        /// <summary>İstemcinin kendi oluşturduğu HttpClient'ı kapatır.</summary>
        public void Dispose()
        {
            if (_ownsHttp)
            {
                _http.Dispose();
            }
        }

        /// <summary>SDK identity verification için <c>identity_hash = HMAC-SHA256(sdkSecret, externalId)</c> (hex).</summary>
        public static string IdentityHash(string sdkSecret, string externalId)
        {
            var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(sdkSecret), Encoding.UTF8.GetBytes(externalId));
            return Convert.ToHexString(mac).ToLowerInvariant();
        }

        /// <summary>
        /// Düşük seviyeli istek: SDK'da metodu olmayan uçlar veya JSON dışı yanıtlar (ör. analitik <c>format=csv</c>) için.
        /// <paramref name="path"/> "/v1/…" biçimindedir; <c>{appId}</c> varsa app kimliğiyle doldurulur. Yanıt gövdesi metin olarak döner.
        /// </summary>
        public async Task<string> RequestAsync(string method, string path, IDictionary<string, object?>? query = null, object? body = null,
            RequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            var op = new Operation(method.ToUpperInvariant(), path) { Body = body };
            if (query is not null)
            {
                foreach (var kv in query)
                {
                    op.Query(kv.Key, kv.Value);
                }
            }
            var raw = await SendAsync(op, options, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(raw);
        }

        internal async Task<T> ExecuteAsync<T>(Operation op, RequestOptions? options, CancellationToken cancellationToken)
        {
            var raw = await SendAsync(op, options, cancellationToken).ConfigureAwait(false);
            if (raw.Length == 0)
            {
                return default!;
            }
            try
            {
                return JsonSerializer.Deserialize<T>(raw, Json.Options)!;
            }
            catch (JsonException e)
            {
                throw new PushizoException(200, "DECODE", "Yanıt ayrıştırılamadı: " + e.Message, inner: e);
            }
        }

        internal async Task ExecuteAsync(Operation op, RequestOptions? options, CancellationToken cancellationToken)
        {
            await SendAsync(op, options, cancellationToken).ConfigureAwait(false);
        }

        private string ResolveAppId(RequestOptions? options)
        {
            var id = options?.AppId ?? _options.AppId;
            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidOperationException("Pushizo: AppId verilmedi (PushizoOptions.AppId veya RequestOptions.AppId).");
            }
            return id!;
        }

        private async Task<byte[]> SendAsync(Operation op, RequestOptions? options, CancellationToken cancellationToken)
        {
            var path = op.Path;
            if (path.Contains("{appId}"))
            {
                path = path.Replace("{appId}", Uri.EscapeDataString(ResolveAppId(options)));
            }
            foreach (var p in op.PathParams)
            {
                path = path.Replace("{" + p.Key + "}", Uri.EscapeDataString(p.Value));
            }
            var url = new StringBuilder(_baseUrl).Append(path);
            var sep = '?';
            if (op.QueryAppId)
            {
                url.Append(sep).Append("app_id=").Append(Uri.EscapeDataString(ResolveAppId(options)));
                sep = '&';
            }
            foreach (var q in op.QueryItems)
            {
                url.Append(sep).Append(Uri.EscapeDataString(q.Key)).Append('=').Append(Uri.EscapeDataString(q.Value));
                sep = '&';
            }

            var key = options?.IdempotencyKey;
            byte[]? body = null;
            string? contentType = null;
            if (op.IsMultipart)
            {
                var boundary = "----pushizo" + Guid.NewGuid().ToString("N");
                body = Multipart(op.Form, boundary);
                contentType = "multipart/form-data; boundary=" + boundary;
            }
            else if (op.Body is not null)
            {
                var node = JsonSerializer.SerializeToNode(op.Body, op.Body.GetType(), Json.Options);
                if (node is JsonObject obj)
                {
                    if (op.BodyAppId && string.IsNullOrEmpty(ReadString(obj, "app_id")))
                    {
                        obj["app_id"] = ResolveAppId(options);
                    }
                    if (string.IsNullOrEmpty(key) && op.Idempotent)
                    {
                        key = ReadString(obj, "idempotency_key");
                    }
                }
                body = Encoding.UTF8.GetBytes(node is null ? "null" : node.ToJsonString(Json.Options));
                contentType = "application/json";
            }
            if (string.IsNullOrEmpty(key) && op.Idempotent && _options.AutoIdempotencyKey)
            {
                key = Guid.NewGuid().ToString();
            }

            var timeout = options?.Timeout ?? _options.Timeout;
            var maxRetries = options?.MaxRetries ?? _options.MaxRetries;
            var target = url.ToString();
            var hasKey = !string.IsNullOrEmpty(key)
                || (options is not null && options.Headers.Any(h => string.Equals(h.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(h.Value)));
            for (var attempt = 0; ; attempt++)
            {
                PushizoException error;
                var notSent = false;
                using (var request = new HttpRequestMessage(new HttpMethod(op.Method), target))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _options.ApiKey);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                    if (!string.IsNullOrEmpty(key))
                    {
                        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
                    }
                    if (options is not null)
                    {
                        foreach (var h in options.Headers)
                        {
                            request.Headers.Remove(h.Key);
                            request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                        }
                    }
                    if (body is not null)
                    {
                        request.Content = new ByteArrayContent(body);
                        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType!);
                    }

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (timeout > TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
                    {
                        cts.CancelAfter(timeout);
                    }
                    HttpResponseMessage? response = null;
                    try
                    {
                        response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                        var data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                        var status = (int)response.StatusCode;
                        if (status >= 200 && status < 300)
                        {
                            return data;
                        }
                        error = ErrorFrom(response, data);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException e)
                    {
                        error = new NetworkException("İstek zaman aşımına uğradı.", e);
                    }
                    catch (HttpRequestException e)
                    {
                        error = new NetworkException(e.Message, e);
                        notSent = RequestNotSent(e);
                    }
                    catch (IOException e)
                    {
                        error = new NetworkException(e.Message, e);
                    }
                    finally
                    {
                        response?.Dispose();
                    }
                }
                if (attempt >= maxRetries || !ShouldRetry(op.Method, error, hasKey, notSent))
                {
                    throw error;
                }
                var delay = error.RetryAfter is { } ra && ra > TimeSpan.Zero ? ra : Backoff[Math.Min(attempt, Backoff.Length - 1)];
                await Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Yeniden deneme politikası: 429 her metotta (istek işlenmeden reddedilmiştir); 5xx ve ağ hataları idempotent
        /// metotlarda, POST/PATCH'te yalnızca Idempotency-Key varsa ya da istek sunucuya hiç gönderilmediyse. Aksi hâlde
        /// sunucu isteği işlemiş olabilir ve tekrar çift kayıt üretebilir.
        /// </summary>
        internal static bool ShouldRetry(string method, PushizoException error, bool hasIdempotencyKey, bool notSent)
        {
            if (error.Status == 429)
            {
                return true;
            }
            if (!error.IsRetryable)
            {
                return false;
            }
            return IdempotentMethods.Contains(method) || hasIdempotencyKey || notSent;
        }

        /// <summary>
        /// Hatanın bağlantı kurulmadan (DNS çözümlenemedi, bağlantı reddi, ağ/sunucu erişilemez) oluştuğunu, yani isteğin
        /// sunucuya hiç gönderilmediğini bildirir.
        /// </summary>
        internal static bool RequestNotSent(HttpRequestException e)
        {
            if (e.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
            {
                return true;
            }
            for (Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is SocketException se && se.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostNotFound
                    or SocketError.TryAgain or SocketError.NoData or SocketError.HostUnreachable or SocketError.NetworkUnreachable)
                {
                    return true;
                }
            }
            return false;
        }

        private static string? ReadString(JsonObject obj, string name)
        {
            if (obj.TryGetPropertyValue(name, out var v) && v is JsonValue value && value.TryGetValue<string>(out var s))
            {
                return s;
            }
            return null;
        }

        private static PushizoException ErrorFrom(HttpResponseMessage response, byte[] data)
        {
            string? code = null, message = null, requestId = null;
            Dictionary<string, object?>? details = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    message = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    requestId = err.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    if (err.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object)
                    {
                        details = d.EnumerateObject().ToDictionary(x => x.Name, x => ToPlain(x.Value));
                    }
                }
            }
            catch (JsonException)
            {
                // Zarf JSON değilse HTTP durumundan türetilir.
            }
            var status = (int)response.StatusCode;
            if (requestId is null && response.Headers.TryGetValues("x-request-id", out var ids))
            {
                requestId = ids.FirstOrDefault();
            }
            TimeSpan? retryAfter = null;
            var ra = response.Headers.RetryAfter;
            if (ra?.Delta is { } delta && delta > TimeSpan.Zero)
            {
                retryAfter = delta;
            }
            else if (ra?.Date is { } date && date > DateTimeOffset.UtcNow)
            {
                retryAfter = date - DateTimeOffset.UtcNow;
            }
            return PushizoException.Create(status, code ?? $"HTTP_{status}", message ?? $"HTTP {status}", requestId, details, retryAfter);
        }

        /// <summary>JsonElement'i sade .NET değerine çevirir (hata ayrıntıları için).</summary>
        private static object? ToPlain(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.Array => e.EnumerateArray().Select(ToPlain).ToList(),
            _ => e.EnumerateObject().ToDictionary(x => x.Name, x => ToPlain(x.Value)),
        };

        private static byte[] Multipart(List<(string Name, string? Value, FileUpload? File)> parts, string boundary)
        {
            using var ms = new MemoryStream();
            void Write(string s)
            {
                var b = Encoding.UTF8.GetBytes(s);
                ms.Write(b, 0, b.Length);
            }
            static string Quote(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", string.Empty).Replace("\n", string.Empty);
            foreach (var p in parts)
            {
                Write($"--{boundary}\r\n");
                if (p.File is null)
                {
                    Write($"Content-Disposition: form-data; name=\"{Quote(p.Name)}\"\r\n\r\n");
                    Write(p.Value ?? string.Empty);
                }
                else
                {
                    Write($"Content-Disposition: form-data; name=\"{Quote(p.Name)}\"; filename=\"{Quote(p.File.Filename)}\"\r\n");
                    Write($"Content-Type: {p.File.ContentType}\r\n\r\n");
                    ms.Write(p.File.Content, 0, p.File.Content.Length);
                }
                Write("\r\n");
            }
            Write($"--{boundary}--\r\n");
            return ms.ToArray();
        }
    }
}
