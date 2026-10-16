using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Pushizo.Tests
{
    /// <summary>SDK JWT identity verification belirteci (ES256).</summary>
    public class IdentityTokenTests
    {
        private static byte[] FromBase64Url(string value)
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }

        [Fact]
        public void BaslikTaleplerVeImzaDogrulanir()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var token = IdentityToken.Create(key.ExportPkcs8PrivateKeyPem(), "main", "app_1", "kullanici-1", TimeSpan.FromMinutes(10), DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
            var parts = token.Split('.');
            Assert.Equal(3, parts.Length);
            using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
            Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
            Assert.Equal("main", header.RootElement.GetProperty("kid").GetString());
            using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
            Assert.Equal("kullanici-1", payload.RootElement.GetProperty("sub").GetString());
            Assert.Equal("app_1", payload.RootElement.GetProperty("aud").GetString());
            Assert.Equal(1_800_000_600, payload.RootElement.GetProperty("exp").GetInt64());
            var signature = FromBase64Url(parts[2]);
            Assert.Equal(64, signature.Length);
            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256));
        }

        [Fact]
        public void OmurSiniriUygulanir()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.Throws<ArgumentOutOfRangeException>(() => IdentityToken.Create(key.ExportECPrivateKeyPem(), "k", "a", "e", TimeSpan.FromHours(25)));
            Assert.Equal(3, IdentityToken.Create(key.ExportECPrivateKeyPem(), "k", "a", "e").Split('.').Length);
        }
    }
}
