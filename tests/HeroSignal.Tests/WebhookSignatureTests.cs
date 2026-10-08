using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeroSignal.Webhooks;
using Xunit;

namespace HeroSignal.Tests
{
    /// <summary>sdk/testdata/webhook-signature-vectors.json ortak vektörleri (Node/PHP/Python/Go/Java/Ruby ile aynı).</summary>
    public class WebhookSignatureTests
    {
        private static readonly JsonElement[] Cases = LoadCases();

        private static JsonElement[] LoadCases()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "testdata", "webhook-signature-vectors.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return doc.RootElement.GetProperty("cases").EnumerateArray().Select(e => e.Clone()).ToArray();
        }

        public static IEnumerable<object[]> CaseNames() => Cases.Select(c => new object[] { c.GetProperty("name").GetString()! });

        private static JsonElement Find(string name) => Cases.Single(c => c.GetProperty("name").GetString() == name);

        [Theory]
        [MemberData(nameof(CaseNames))]
        public void SharedVector(string name)
        {
            var c = Find(name);
            var payload = c.GetProperty("payload").GetString()!;
            var header = c.GetProperty("header").ValueKind == JsonValueKind.Null ? null : c.GetProperty("header").GetString();
            var secrets = c.GetProperty("secrets").EnumerateArray().Select(s => s.GetString()!).ToArray();
            var tolerance = TimeSpan.FromSeconds(c.GetProperty("toleranceSeconds").GetInt64());
            var now = DateTimeOffset.FromUnixTimeSeconds(c.GetProperty("now").GetInt64());
            var expected = c.GetProperty("expected");

            var result = WebhookSignature.Verify(payload, header, secrets, tolerance, now);
            Assert.Equal(expected.GetProperty("ok").GetBoolean(), result.Ok);
            if (result.Ok)
            {
                Assert.Equal(expected.GetProperty("timestamp").GetInt64(), result.Timestamp);
                if (c.TryGetProperty("events", out var count))
                {
                    var events = WebhookSignature.ConstructEvents(Encoding.UTF8.GetBytes(payload), header, secrets, tolerance, now);
                    Assert.Equal(count.GetInt32(), events.Count);
                    Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Type)));
                }
            }
            else
            {
                Assert.Equal(expected.GetProperty("reason").GetString(), result.ReasonCode);
                var ex = Assert.Throws<WebhookSignatureException>(() =>
                    WebhookSignature.ConstructEvents(Encoding.UTF8.GetBytes(payload), header, secrets, tolerance, now));
                Assert.Equal(expected.GetProperty("reason").GetString(), ex.ReasonCode);
            }
        }

        [Fact]
        public void MalformedBodyAfterValidSignature()
        {
            var body = Encoding.UTF8.GetBytes("not json");
            var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s"), Encoding.UTF8.GetBytes("1800000000.not json"))).ToLowerInvariant();
            var ex = Assert.Throws<WebhookSignatureException>(() =>
                WebhookSignature.ConstructEvents(body, $"t=1800000000,v1={sig}", new[] { "s" }, null, DateTimeOffset.FromUnixTimeSeconds(1800000000)));
            Assert.Equal(WebhookVerifyFailure.MalformedBody, ex.Reason);
            Assert.Equal("malformed_body", ex.ReasonCode);
        }

        [Fact]
        public void DefaultToleranceUsesCurrentTime()
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = "{\"id\":\"whe_1\",\"type\":\"x\"}";
            string Sign(long t) => $"t={t},v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s"), Encoding.UTF8.GetBytes($"{t}.{payload}"))).ToLowerInvariant();
            Assert.True(WebhookSignature.Verify(payload, Sign(ts), new[] { "s" }).Ok);
            Assert.Equal(WebhookVerifyFailure.TimestampOutOfTolerance, WebhookSignature.Verify(payload, Sign(ts - 400), new[] { "s" }).Reason);
        }
    }
}
