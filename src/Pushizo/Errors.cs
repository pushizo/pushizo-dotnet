using System;
using System.Collections.Generic;

namespace Pushizo
{
    /// <summary>API hata zarfındaki <c>error.code</c> değerleri (HS_ önekli katalog).</summary>
    public static class ErrorCodes
    {
        public const string ValidationError = "HS_VALIDATION_ERROR";
        public const string BadRequest = "HS_BAD_REQUEST";
        public const string Unauthenticated = "HS_UNAUTHENTICATED";
        public const string Forbidden = "HS_FORBIDDEN";
        public const string TenantMismatch = "HS_TENANT_MISMATCH";
        public const string NotFound = "HS_NOT_FOUND";
        public const string Conflict = "HS_CONFLICT";
        public const string IdempotencyConflict = "HS_IDEMPOTENCY_CONFLICT";
        public const string IdempotencyInProgress = "HS_IDEMPOTENCY_IN_PROGRESS";
        public const string NoRecipients = "HS_NO_RECIPIENTS";
        public const string ChannelNotConfigured = "HS_CHANNEL_NOT_CONFIGURED";
        public const string ContentInvalid = "HS_CONTENT_INVALID";
        public const string SubscriptionInvalid = "HS_SUBSCRIPTION_INVALID";
        public const string Unprocessable = "HS_UNPROCESSABLE";
        public const string RateLimited = "HS_RATE_LIMITED";
        public const string QuotaExceeded = "HS_QUOTA_EXCEEDED";
        public const string Internal = "HS_INTERNAL";
        public const string NotImplemented = "HS_NOT_IMPLEMENTED";
        public const string ProviderUnavailable = "HS_PROVIDER_UNAVAILABLE";
        public const string ServiceUnavailable = "HS_SERVICE_UNAVAILABLE";

        /// <summary>İstemci tarafı: bağlantı kurulamadı veya zaman aşımı (Status 0).</summary>
        public const string Network = "NETWORK";
    }

    /// <summary>
    /// API hata zarfından (<c>{"error": {"code", "message", "request_id", "details"}}</c>) türetilen istisna.
    /// HTTP durumuna göre alt sınıflar fırlatılır (<see cref="NotFoundException"/>, <see cref="RateLimitException"/> …).
    /// </summary>
    public class PushizoException : Exception
    {
        public PushizoException(int status, string code, string message, string? requestId = null,
            IReadOnlyDictionary<string, object?>? details = null, TimeSpan? retryAfter = null, Exception? inner = null)
            : base(message, inner)
        {
            Status = status;
            Code = code;
            RequestId = requestId;
            Details = details;
            RetryAfter = retryAfter;
        }

        /// <summary>HTTP durumu (ağ hatasında 0).</summary>
        public int Status { get; }

        /// <summary>HS_ hata kodu (<see cref="ErrorCodes"/>); zarf yoksa <c>HTTP_&lt;durum&gt;</c>.</summary>
        public string Code { get; }

        public string? RequestId { get; }

        public IReadOnlyDictionary<string, object?>? Details { get; }

        /// <summary>429/503 yanıtındaki Retry-After (yoksa null).</summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>429, 5xx veya ağ hatası: yeniden denenebilir.</summary>
        public bool IsRetryable => Status == 0 || Status == 429 || Status >= 500;

        /// <inheritdoc />
        public override string ToString() =>
            $"{GetType().Name}[{Status} {Code}] {Message}" + (RequestId is null ? string.Empty : $" (request_id={RequestId})");

        /// <summary>HTTP durumuna karşılık gelen alt sınıfı oluşturur.</summary>
        public static PushizoException Create(int status, string code, string message, string? requestId,
            IReadOnlyDictionary<string, object?>? details, TimeSpan? retryAfter)
        {
            switch (status)
            {
                case 400:
                case 422:
                    return new InvalidRequestException(status, code, message, requestId, details);
                case 401:
                    return new AuthenticationException(status, code, message, requestId, details);
                case 403:
                    return new PermissionDeniedException(status, code, message, requestId, details);
                case 404:
                    return new NotFoundException(status, code, message, requestId, details);
                case 409:
                    return new ConflictException(status, code, message, requestId, details);
                case 429:
                    return new RateLimitException(status, code, message, requestId, details, retryAfter);
            }
            if (status >= 500)
            {
                return new ServerException(status, code, message, requestId, details, retryAfter);
            }
            return new PushizoException(status, code, message, requestId, details, retryAfter);
        }
    }

    /// <summary>Bağlantı kurulamadı veya zaman aşımı (Status 0).</summary>
    public sealed class NetworkException : PushizoException
    {
        public NetworkException(string message, Exception? inner = null) : base(0, ErrorCodes.Network, message, inner: inner) { }
    }

    /// <summary>400 / 422: istek doğrulanamadı veya işlenemedi (Details alan hatalarını içerir).</summary>
    public sealed class InvalidRequestException : PushizoException
    {
        public InvalidRequestException(int status, string code, string message, string? requestId, IReadOnlyDictionary<string, object?>? details)
            : base(status, code, message, requestId, details) { }
    }

    /// <summary>401: API anahtarı eksik veya geçersiz.</summary>
    public sealed class AuthenticationException : PushizoException
    {
        public AuthenticationException(int status, string code, string message, string? requestId, IReadOnlyDictionary<string, object?>? details)
            : base(status, code, message, requestId, details) { }
    }

    /// <summary>403: anahtarın kapsamı yetersiz, IP izin listesi dışı veya kaynak başka kiracıya ait.</summary>
    public sealed class PermissionDeniedException : PushizoException
    {
        public PermissionDeniedException(int status, string code, string message, string? requestId, IReadOnlyDictionary<string, object?>? details)
            : base(status, code, message, requestId, details) { }
    }

    /// <summary>404: kaynak bulunamadı.</summary>
    public sealed class NotFoundException : PushizoException
    {
        public NotFoundException(int status, string code, string message, string? requestId, IReadOnlyDictionary<string, object?>? details)
            : base(status, code, message, requestId, details) { }
    }

    /// <summary>409: kaynak çakışması veya idempotency anahtarı çakışması.</summary>
    public sealed class ConflictException : PushizoException
    {
        public ConflictException(int status, string code, string message, string? requestId, IReadOnlyDictionary<string, object?>? details)
            : base(status, code, message, requestId, details) { }
    }

    /// <summary>429: hız sınırı veya plan kotası aşıldı.</summary>
    public sealed class RateLimitException : PushizoException
    {
        public RateLimitException(int status, string code, string message, string? requestId, IReadOnlyDictionary<string, object?>? details, TimeSpan? retryAfter)
            : base(status, code, message, requestId, details, retryAfter) { }
    }

    /// <summary>5xx: sunucu veya sağlayıcı geçici olarak kullanılamıyor.</summary>
    public sealed class ServerException : PushizoException
    {
        public ServerException(int status, string code, string message, string? requestId, IReadOnlyDictionary<string, object?>? details, TimeSpan? retryAfter)
            : base(status, code, message, requestId, details, retryAfter) { }
    }

    /// <summary>Webhook imzası geçersiz veya gövde ayrıştırılamadı (<c>malformed_body</c>).</summary>
    public sealed class WebhookSignatureException : Exception
    {
        public WebhookSignatureException(string reason) : base($"Pushizo webhook imzası geçersiz: {reason}")
        {
            Reason = reason;
        }

        /// <summary><c>missing_header</c>, <c>malformed_header</c>, <c>timestamp_out_of_tolerance</c>, <c>signature_mismatch</c> veya <c>malformed_body</c>.</summary>
        public string Reason { get; }
    }
}
