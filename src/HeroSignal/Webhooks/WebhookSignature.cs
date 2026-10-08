using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeroSignal.Webhooks
{
    /// <summary>Webhook doğrulama başarısızlık nedeni (tüm SDK'larda aynı kodlar; bkz. <see cref="WebhookVerifyResult.ReasonCode"/>).</summary>
    public enum WebhookVerifyFailure
    {
        /// <summary>Başlık yok veya boş (<c>missing_header</c>).</summary>
        MissingHeader,
        /// <summary><c>t</c> veya geçerli bir <c>v1</c> yok (<c>malformed_header</c>).</summary>
        MalformedHeader,
        /// <summary>Zaman damgası tolerans dışında (<c>timestamp_out_of_tolerance</c>).</summary>
        TimestampOutOfTolerance,
        /// <summary>Hiçbir secret ile imza eşleşmedi (<c>signature_mismatch</c>).</summary>
        SignatureMismatch,
        /// <summary>İmza geçerli ama gövde JSON nesnesi değil (<c>malformed_body</c>).</summary>
        MalformedBody,
    }

    /// <summary>Webhook imza doğrulama sonucu.</summary>
    public sealed class WebhookVerifyResult
    {
        private WebhookVerifyResult(bool ok, long timestamp, WebhookVerifyFailure? reason)
        {
            Ok = ok;
            Timestamp = timestamp;
            Reason = reason;
        }

        /// <summary>İmza geçerli mi.</summary>
        public bool Ok { get; }

        /// <summary>İmzadaki Unix zaman damgası (geçerliyse).</summary>
        public long Timestamp { get; }

        /// <summary>Başarısızlık nedeni (geçerliyse null).</summary>
        public WebhookVerifyFailure? Reason { get; }

        /// <summary>SDK'lar arası ortak neden kodu (ör. <c>signature_mismatch</c>); geçerliyse null.</summary>
        public string? ReasonCode => Reason.HasValue ? WebhookSignature.Code(Reason.Value) : null;

        internal static WebhookVerifyResult Success(long timestamp) => new(true, timestamp, null);

        internal static WebhookVerifyResult Failure(WebhookVerifyFailure reason) => new(false, 0, reason);
    }

    /// <summary>Webhook imzası veya gövdesi geçersiz.</summary>
    public sealed class WebhookSignatureException : Exception
    {
        /// <summary>İstisna oluşturur.</summary>
        /// <param name="reason">Neden.</param>
        public WebhookSignatureException(WebhookVerifyFailure reason)
            : base($"HeroSignal webhook imzası geçersiz: {WebhookSignature.Code(reason)}")
        {
            Reason = reason;
        }

        /// <summary>Başarısızlık nedeni.</summary>
        public WebhookVerifyFailure Reason { get; }

        /// <summary>SDK'lar arası ortak neden kodu.</summary>
        public string ReasonCode => WebhookSignature.Code(Reason);
    }

    /// <summary>Webhook olayı; <see cref="Data"/> olay tipine göre değişen yüktür.</summary>
    public sealed class WebhookEvent
    {
        /// <summary>Olay kimliği (<c>whe_…</c>); tekrarları ayıklamak için.</summary>
        public string? Id { get; init; }

        /// <summary>Olay tipi (ör. <c>user.created</c>).</summary>
        public string? Type { get; init; }

        /// <summary>Oluşturulma zamanı (ISO 8601).</summary>
        public string? CreatedAt { get; init; }

        /// <summary>App kimliği.</summary>
        public string? AppId { get; init; }

        /// <summary>Olay verisi.</summary>
        public JsonElement Data { get; init; }
    }

    /// <summary>
    /// Giden webhook imza doğrulaması (alıcı tarafı). Başlık: <c>HeroSignal-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c>;
    /// <c>v1 = HMAC-SHA256(secret, t + "." + ham gövde)</c>. Secret rotasyonunda başlıkta iki <c>v1</c> bulunabilir;
    /// herhangi biri eşleşirse imza geçerlidir. Karşılaştırma sabit zamanlıdır.
    /// </summary>
    /// <example>
    /// <code>
    /// app.MapPost("/hooks/herosignal", async (HttpRequest req) =>
    /// {
    ///     using var ms = new MemoryStream();
    ///     await req.Body.CopyToAsync(ms); // HAM gövde
    ///     var events = WebhookSignature.ConstructEvents(ms.ToArray(), req.Headers[WebhookSignature.SignatureHeader], new[] { secret });
    ///     return Results.NoContent();
    /// });
    /// </code>
    /// </example>
    public static class WebhookSignature
    {
        /// <summary>İmza başlığı.</summary>
        public const string SignatureHeader = "HeroSignal-Signature";

        /// <summary>Olay kimliği başlığı; tekrar gönderimleri ayıklamak için kullanın.</summary>
        public const string EventIdHeader = "HeroSignal-Event-Id";

        /// <summary>Varsayılan zaman damgası toleransı; daha eski <c>t</c> tekrar (replay) sayılır.</summary>
        public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

        internal static string Code(WebhookVerifyFailure reason) => reason switch
        {
            WebhookVerifyFailure.MissingHeader => "missing_header",
            WebhookVerifyFailure.MalformedHeader => "malformed_header",
            WebhookVerifyFailure.TimestampOutOfTolerance => "timestamp_out_of_tolerance",
            WebhookVerifyFailure.SignatureMismatch => "signature_mismatch",
            _ => "malformed_body",
        };

        /// <summary>İmzayı doğrular.</summary>
        /// <param name="payload">HAM istek gövdesi (JSON'u ayrıştırıp yeniden serileştirmeyin).</param>
        /// <param name="header"><c>HeroSignal-Signature</c> değeri.</param>
        /// <param name="secrets">Endpoint secret'ları (rotasyonda eski ve yeni).</param>
        /// <param name="tolerance">Zaman damgası toleransı (null ise 300 sn).</param>
        /// <param name="now">Şimdiki zaman (test için; null ise şimdi).</param>
        public static WebhookVerifyResult Verify(byte[] payload, string? header, IEnumerable<string> secrets, TimeSpan? tolerance = null, DateTimeOffset? now = null)
        {
            if (string.IsNullOrEmpty(header)) return WebhookVerifyResult.Failure(WebhookVerifyFailure.MissingHeader);
            long? timestamp = null;
            var signatures = new List<byte[]>();
            foreach (var part in header!.Split(','))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                var key = part.Substring(0, idx).Trim();
                var value = part.Substring(idx + 1).Trim();
                if (key == "t" && IsDigits(value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
                {
                    timestamp = t;
                }
                else if (key == "v1" && IsLowerHex64(value))
                {
                    signatures.Add(Convert.FromHexString(value));
                }
            }
            if (timestamp == null || signatures.Count == 0) return WebhookVerifyResult.Failure(WebhookVerifyFailure.MalformedHeader);
            var nowSec = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var tol = (long)(tolerance ?? DefaultTolerance).TotalSeconds;
            if (Math.Abs(nowSec - timestamp.Value) > tol) return WebhookVerifyResult.Failure(WebhookVerifyFailure.TimestampOutOfTolerance);
            var prefix = Encoding.ASCII.GetBytes(timestamp.Value.ToString(CultureInfo.InvariantCulture) + ".");
            var signed = new byte[prefix.Length + payload.Length];
            Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, signed, prefix.Length, payload.Length);
            foreach (var secret in secrets)
            {
                var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
                foreach (var sig in signatures)
                {
                    if (CryptographicOperations.FixedTimeEquals(sig, expected)) return WebhookVerifyResult.Success(timestamp.Value);
                }
            }
            return WebhookVerifyResult.Failure(WebhookVerifyFailure.SignatureMismatch);
        }

        /// <summary>Metin gövde için kolaylık (UTF-8 baytları imzalanır).</summary>
        /// <param name="payload">HAM gövde metni.</param>
        /// <param name="header">Başlık.</param>
        /// <param name="secrets">Secret'lar.</param>
        /// <param name="tolerance">Tolerans.</param>
        /// <param name="now">Şimdiki zaman.</param>
        public static WebhookVerifyResult Verify(string payload, string? header, IEnumerable<string> secrets, TimeSpan? tolerance = null, DateTimeOffset? now = null) =>
            Verify(Encoding.UTF8.GetBytes(payload), header, secrets, tolerance, now);

        /// <summary>İmzayı doğrular ve gövdeyi olay listesine çevirir (tekil gövde veya <c>{"events": [...]}</c> batch).</summary>
        /// <param name="payload">HAM gövde.</param>
        /// <param name="header">Başlık.</param>
        /// <param name="secrets">Secret'lar.</param>
        /// <param name="tolerance">Tolerans.</param>
        /// <param name="now">Şimdiki zaman.</param>
        /// <exception cref="WebhookSignatureException">İmza veya gövde geçersizse.</exception>
        public static IReadOnlyList<WebhookEvent> ConstructEvents(byte[] payload, string? header, IEnumerable<string> secrets, TimeSpan? tolerance = null, DateTimeOffset? now = null)
        {
            var result = Verify(payload, header, secrets, tolerance, now);
            if (!result.Ok) throw new WebhookSignatureException(result.Reason!.Value);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                throw new WebhookSignatureException(WebhookVerifyFailure.MalformedBody);
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new WebhookSignatureException(WebhookVerifyFailure.MalformedBody);
                var events = new List<WebhookEvent>();
                if (root.TryGetProperty("events", out var batch) && batch.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in batch.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.Object) events.Add(ToEvent(e));
                    }
                }
                else
                {
                    events.Add(ToEvent(root));
                }
                return events;
            }
        }

        private static WebhookEvent ToEvent(JsonElement e) => new()
        {
            Id = Str(e, "id"),
            Type = Str(e, "type"),
            CreatedAt = Str(e, "created_at"),
            AppId = Str(e, "app_id"),
            Data = e.TryGetProperty("data", out var d) ? d.Clone() : default,
        };

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool IsDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (var c in s)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        private static bool IsLowerHex64(string s)
        {
            if (s.Length != 64) return false;
            foreach (var c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
