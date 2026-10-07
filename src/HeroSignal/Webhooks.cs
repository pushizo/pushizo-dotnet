using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HeroSignal
{
    /// <summary>Webhook imza doğrulama sonucu. <see cref="Ok"/> ise <see cref="Timestamp"/>, değilse <see cref="Reason"/> doludur.</summary>
    public sealed record WebhookVerifyResult(bool Ok, long Timestamp, string? Reason);

    /// <summary>Webhook olayı (tekil gövde). Batch gövdesi <c>{"events": [...]}</c> biçimindedir.</summary>
    public sealed class WebhookEvent
    {
        /// <summary>Olay kimliği (<c>HeroSignal-Event-Id</c> ile aynı); tekrarları ayıklamak için kullanın.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>Olay tipi (ör. <c>delivery.delivered</c>).</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("app_id")]
        public string? AppId { get; set; }

        /// <summary>Olaya özgü veri.</summary>
        [JsonPropertyName("data")]
        public JsonElement Data { get; set; }
    }

    /// <summary>
    /// Giden webhook imza doğrulaması (alıcı tarafı). Başlık: <c>HeroSignal-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c> —
    /// <c>v1 = HMAC-SHA256(secret, t + "." + ham_gövde)</c>. Secret rotasyonunda iki <c>v1</c> bulunabilir; herhangi biri eşleşirse geçerlidir.
    /// <code>
    /// using var ms = new MemoryStream(); await Request.Body.CopyToAsync(ms);           // ham gövde
    /// var events = Webhooks.ConstructEvents(ms.ToArray(), Request.Headers[Webhooks.SignatureHeader], new[] { secret });
    /// </code>
    /// </summary>
    public static class Webhooks
    {
        public const string SignatureHeader = "HeroSignal-Signature";
        public const string EventIdHeader = "HeroSignal-Event-Id";

        /// <summary>Varsayılan zaman damgası toleransı; daha eski <c>t</c> tekrar (replay) sayılır.</summary>
        public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

        private static readonly Regex Digits = new Regex("^[0-9]+$", RegexOptions.CultureInvariant);
        private static readonly Regex Signature = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

        /// <summary>Metin gövdeyi UTF-8 baytları üzerinden doğrular.</summary>
        public static WebhookVerifyResult VerifySignature(string payload, string? header, IEnumerable<string> secrets,
            TimeSpan? tolerance = null, DateTimeOffset? now = null)
            => VerifySignature(Encoding.UTF8.GetBytes(payload), header, secrets, tolerance, now);

        /// <summary>
        /// İmzayı doğrular. <paramref name="payload"/> HAM istek gövdesidir (JSON'u ayrıştırıp yeniden serileştirmeyin).
        /// Karşılaştırma sabit zamanlıdır.
        /// </summary>
        public static WebhookVerifyResult VerifySignature(byte[] payload, string? header, IEnumerable<string> secrets,
            TimeSpan? tolerance = null, DateTimeOffset? now = null)
        {
            if (string.IsNullOrEmpty(header))
            {
                return new WebhookVerifyResult(false, 0, "missing_header");
            }
            long? timestamp = null;
            var signatures = new List<byte[]>();
            foreach (var part in header!.Split(','))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }
                var key = part.Substring(0, idx).Trim();
                var value = part.Substring(idx + 1).Trim();
                if (key == "t" && Digits.IsMatch(value) && long.TryParse(value, out var ts))
                {
                    timestamp = ts;
                }
                else if (key == "v1" && Signature.IsMatch(value))
                {
                    signatures.Add(Convert.FromHexString(value));
                }
            }
            if (timestamp is null || signatures.Count == 0)
            {
                return new WebhookVerifyResult(false, 0, "malformed_header");
            }
            var nowSec = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var tol = (long)(tolerance ?? DefaultTolerance).TotalSeconds;
            if (Math.Abs(nowSec - timestamp.Value) > tol)
            {
                return new WebhookVerifyResult(false, 0, "timestamp_out_of_tolerance");
            }
            var prefix = Encoding.ASCII.GetBytes(timestamp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            var signed = new byte[prefix.Length + payload.Length];
            Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
            Buffer.BlockCopy(payload, 0, signed, prefix.Length, payload.Length);
            foreach (var secret in secrets)
            {
                var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
                if (signatures.Any(sig => CryptographicOperations.FixedTimeEquals(sig, expected)))
                {
                    return new WebhookVerifyResult(true, timestamp.Value, null);
                }
            }
            return new WebhookVerifyResult(false, 0, "signature_mismatch");
        }

        /// <summary>Doğrular ve olay listesini döner (tekil veya batch gövde). Geçersizse <see cref="WebhookSignatureException"/>.</summary>
        public static IReadOnlyList<WebhookEvent> ConstructEvents(byte[] payload, string? header, IEnumerable<string> secrets,
            TimeSpan? tolerance = null, DateTimeOffset? now = null)
        {
            var result = VerifySignature(payload, header, secrets, tolerance, now);
            if (!result.Ok)
            {
                throw new WebhookSignatureException(result.Reason ?? "signature_mismatch");
            }
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new WebhookSignatureException("malformed_body");
                }
                if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                {
                    return events.Deserialize<List<WebhookEvent>>(Core.Json.Options) ?? new List<WebhookEvent>();
                }
                var single = root.Deserialize<WebhookEvent>(Core.Json.Options);
                return single is null ? Array.Empty<WebhookEvent>() : new[] { single };
            }
            catch (JsonException)
            {
                throw new WebhookSignatureException("malformed_body");
            }
        }

        /// <summary>Metin gövde için <see cref="ConstructEvents(byte[], string?, IEnumerable{string}, TimeSpan?, DateTimeOffset?)"/>.</summary>
        public static IReadOnlyList<WebhookEvent> ConstructEvents(string payload, string? header, IEnumerable<string> secrets,
            TimeSpan? tolerance = null, DateTimeOffset? now = null)
            => ConstructEvents(Encoding.UTF8.GetBytes(payload), header, secrets, tolerance, now);
    }
}
