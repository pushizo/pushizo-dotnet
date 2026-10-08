using System;
using System.Net.Http;

namespace HeroSignal
{
    /// <summary><see cref="HeroSignalClient"/> ayarları.</summary>
    public sealed class HeroSignalClientOptions
    {
        /// <summary>Varsayılan API adresi.</summary>
        public const string DefaultBaseUrl = "https://api.herosignal.dev";

        /// <summary><c>hs_live_…</c> API anahtarı (zorunlu).</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Çağrılarda verilmezse kullanılacak varsayılan app.</summary>
        public string? AppId { get; set; }

        /// <summary>API adresi; yerel geliştirmede <c>http://localhost:4000</c>.</summary>
        public string BaseUrl { get; set; } = DefaultBaseUrl;

        /// <summary>Deneme başına zaman aşımı (varsayılan 30 sn).</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>429/5xx ve ağ hatalarında en fazla yeniden deneme (varsayılan 3).</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary><c>Idempotency-Key</c> destekleyen çağrılarda anahtar verilmezse UUID üret (varsayılan açık).</summary>
        public bool AutoIdempotencyKey { get; set; } = true;

        /// <summary>User-Agent başlığı (boşsa <c>herosignal-dotnet/&lt;sürüm&gt;</c>).</summary>
        public string? UserAgent { get; set; }

        /// <summary>
        /// Özel <see cref="System.Net.Http.HttpClient"/> (ör. <c>IHttpClientFactory</c>'den). Verilirse ömrü çağırana
        /// aittir ve istemci onu kapatmaz; zaman aşımı istemci tarafından deneme başına uygulanır.
        /// </summary>
        public HttpClient? HttpClient { get; set; }
    }
}
