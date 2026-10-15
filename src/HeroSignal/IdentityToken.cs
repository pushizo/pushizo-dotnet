using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HeroSignal
{
    /// <summary>
    /// SDK JWT identity verification belirteci (ES256; ADR-0023 §9): <c>sub</c> = external_id, <c>aud</c> = app_id,
    /// ömür en çok 24 saat. Uygulamanız belirteci kendi sunucusundan alır ve istemci SDK'sının
    /// <c>login(externalId, jwt)</c> çağrısına verir.
    /// </summary>
    public static class IdentityToken
    {
        /// <summary>En uzun belirteç ömrü.</summary>
        public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

        /// <param name="privateKeyPem">P-256 EC özel anahtarı (PKCS#8 veya SEC1 PEM).</param>
        /// <param name="kid">Panelde kayıtlı genel anahtar kimliği.</param>
        /// <param name="appId">App kimliği (<c>aud</c>).</param>
        /// <param name="externalId">Kullanıcının external_id değeri (<c>sub</c>).</param>
        /// <param name="ttl">Ömür (varsayılan 1 saat).</param>
        /// <param name="now">Testler için sabit zaman.</param>
        public static string Create(string privateKeyPem, string kid, string appId, string externalId, TimeSpan? ttl = null, DateTimeOffset? now = null)
        {
            var lifetime = ttl ?? TimeSpan.FromHours(1);
            if (lifetime < TimeSpan.FromSeconds(1) || lifetime > MaxLifetime)
                throw new ArgumentOutOfRangeException(nameof(ttl), "ttl 1 sn … 24 saat aralığında olmalı.");
            using var key = ECDsa.Create();
            key.ImportFromPem(privateKeyPem);
            if (key.KeySize != 256)
                throw new ArgumentException("ES256 için P-256 EC özel anahtarı gerekli.", nameof(privateKeyPem));
            var iat = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { sub = externalId, aud = appId, iat, exp = iat + (long)lifetime.TotalSeconds }));
            var signingInput = header + "." + payload;
            // .NET varsayılan olarak IEEE P1363 (r||s) biçiminde imzalar; JWS ES256 de bu biçimi bekler.
            var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
            return signingInput + "." + Base64Url(signature);
        }

        private static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
