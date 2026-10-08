using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HeroSignal
{
    /// <summary>
    /// API hata zarfından (<c>{"error": {"code", "message", "request_id", "details"}}</c>) türetilen istisna.
    /// Ağ hatalarında (bağlantı, zaman aşımı) <see cref="Status"/> 0 ve <see cref="Code"/> <c>"NETWORK"</c> olur.
    /// </summary>
    public sealed class HeroSignalException : Exception
    {
        /// <summary>İstisna oluşturur.</summary>
        /// <param name="status">HTTP durum kodu (ağ hatasında 0).</param>
        /// <param name="code">Hata kodu.</param>
        /// <param name="message">Açıklama.</param>
        /// <param name="requestId">İstek kimliği.</param>
        /// <param name="details">Ek ayrıntılar.</param>
        /// <param name="inner">Alttaki hata.</param>
        public HeroSignalException(int status, string code, string message, string? requestId = null, IReadOnlyDictionary<string, JsonElement>? details = null, Exception? inner = null)
            : base(message, inner)
        {
            Status = status;
            Code = code;
            RequestId = requestId;
            Details = details;
        }

        /// <summary>HTTP durum kodu (ağ hatasında 0).</summary>
        public int Status { get; }

        /// <summary>Makine tarafından okunur hata kodu (ör. <c>VALIDATION_FAILED</c>, <c>NETWORK</c>).</summary>
        public string Code { get; }

        /// <summary>Destek taleplerinde kullanılacak istek kimliği (zarf veya <c>X-Request-Id</c>).</summary>
        public string? RequestId { get; }

        /// <summary>Alan bazlı ek bilgiler.</summary>
        public IReadOnlyDictionary<string, JsonElement>? Details { get; }

        /// <summary>429, 5xx veya ağ hatası: yeniden denenebilir.</summary>
        public bool IsRetryable => Status == 0 || Status == 429 || Status >= 500;

        /// <inheritdoc />
        public override string ToString() =>
            $"HeroSignalException[{Status} {Code}] {Message}" + (RequestId != null ? $" (request_id={RequestId})" : string.Empty);
    }
}
