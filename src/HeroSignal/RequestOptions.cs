using System;
using System.Collections.Generic;

namespace HeroSignal
{
    /// <summary>Tek bir çağrının istemci ayarlarını geçersiz kılar.</summary>
    public sealed class RequestOptions
    {
        /// <summary>Bu çağrı için app kimliği (boşsa istemcinin varsayılanı).</summary>
        public string? AppId { get; set; }

        /// <summary><c>Idempotency-Key</c> başlığı (ör. <c>order-42</c>); yeniden denemelerde aynı anahtar kullanılır.</summary>
        public string? IdempotencyKey { get; set; }

        /// <summary>Ek HTTP başlıkları.</summary>
        public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

        /// <summary>En fazla yeniden deneme (null ise istemcinin değeri).</summary>
        public int? MaxRetries { get; set; }

        /// <summary>Deneme başına zaman aşımı (null ise istemcinin değeri).</summary>
        public TimeSpan? Timeout { get; set; }
    }
}
