using System;
using System.Collections.Generic;

namespace HeroSignal
{
    /// <summary>
    /// Tek bir çağrı için istemci ayarlarını değiştirir.
    /// <code>
    /// await hs.Notifications.CreateAsync(body, new RequestOptions { AppId = "app_2", IdempotencyKey = "order-42" });
    /// </code>
    /// </summary>
    public sealed class RequestOptions
    {
        /// <summary>Bu çağrının app kimliği (istemcinin varsayılan app'i yerine).</summary>
        public string? AppId { get; set; }

        /// <summary><c>Idempotency-Key</c> başlığı: aynı anahtarla 24 saat içindeki tekrar aynı yanıtı döner.</summary>
        public string? IdempotencyKey { get; set; }

        /// <summary>Deneme başına zaman aşımı.</summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>429/5xx ve ağ hatalarında en fazla yeniden deneme.</summary>
        public int? MaxRetries { get; set; }

        /// <summary>Ek HTTP başlıkları.</summary>
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

        /// <summary>Yalnızca app kimliğini değiştiren kısayol.</summary>
        public static RequestOptions ForApp(string appId) => new RequestOptions { AppId = appId };
    }

    /// <summary><see cref="HeroSignalClient"/> ayarları.</summary>
    public sealed class HeroSignalOptions
    {
        /// <summary><c>hs_live_…</c> API anahtarı (Bearer). Zorunlu.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>API adresi; yerel geliştirmede <c>http://localhost:4000</c>.</summary>
        public string BaseUrl { get; set; } = HeroSignalClient.DefaultBaseUrl;

        /// <summary>Çağrılarda <see cref="RequestOptions.AppId"/> verilmezse kullanılacak varsayılan app.</summary>
        public string? AppId { get; set; }

        /// <summary>Deneme başına zaman aşımı (varsayılan 30 sn).</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>429/5xx ve ağ hatalarında en fazla yeniden deneme (varsayılan 3).</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>Idempotency-Key destekleyen çağrılarda anahtar verilmezse UUID üret (varsayılan true).</summary>
        public bool AutoIdempotencyKey { get; set; } = true;

        /// <summary>User-Agent başlığı (varsayılan <c>herosignal-dotnet/&lt;sürüm&gt;</c>).</summary>
        public string? UserAgent { get; set; }

        /// <summary>
        /// Özel <see cref="System.Net.Http.HttpClient"/> (IHttpClientFactory, proxy, test işleyicisi). Verilirse ömrü
        /// çağırana aittir; verilmezse istemci kendi örneğini oluşturur ve <see cref="HeroSignalClient.Dispose"/> ile kapatır.
        /// </summary>
        public System.Net.Http.HttpClient? HttpClient { get; set; }
    }
}
