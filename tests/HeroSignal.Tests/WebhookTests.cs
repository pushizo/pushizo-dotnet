using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HeroSignal.Tests
{
    /// <summary>Ortak vektörler: sdk/testdata/webhook-signature-vectors.json (Node, PHP, Python, Go, Java, Ruby ile aynı).</summary>
    public class WebhookTests
    {
        private static string VectorsPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "testdata", "webhook-signature-vectors.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("sdk/testdata/webhook-signature-vectors.json bulunamadı");
        }

        public static IEnumerable<object[]> Cases()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(VectorsPath()));
            foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                yield return new object[] { c.GetProperty("name").GetString()!, c.GetRawText() };
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void SharedVector(string name, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var c = doc.RootElement;
            var payload = Encoding.UTF8.GetBytes(c.GetProperty("payload").GetString()!);
            var header = c.GetProperty("header").ValueKind == JsonValueKind.Null ? null : c.GetProperty("header").GetString();
            var secrets = c.GetProperty("secrets").EnumerateArray().Select(s => s.GetString()!).ToList();
            var tolerance = TimeSpan.FromSeconds(c.GetProperty("toleranceSeconds").GetInt64());
            var now = DateTimeOffset.FromUnixTimeSeconds(c.GetProperty("now").GetInt64());
            var expected = c.GetProperty("expected");

            var result = Webhooks.VerifySignature(payload, header, secrets, tolerance, now);
            Assert.True(expected.GetProperty("ok").GetBoolean() == result.Ok, name);
            if (result.Ok)
            {
                Assert.Equal(expected.GetProperty("timestamp").GetInt64(), result.Timestamp);
                var events = Webhooks.ConstructEvents(payload, header, secrets, tolerance, now);
                if (c.TryGetProperty("events", out var count))
                {
                    Assert.Equal(count.GetInt32(), events.Count);
                }
            }
            else
            {
                Assert.Equal(expected.GetProperty("reason").GetString(), result.Reason);
                var ex = Assert.Throws<WebhookSignatureException>(() => Webhooks.ConstructEvents(payload, header, secrets, tolerance, now));
                Assert.Equal(expected.GetProperty("reason").GetString(), ex.Reason);
            }
        }

        [Fact]
        public void IdentityHashMatchesNode()
        {
            // Node: createHmac('sha256', 'secret').update('user-1').digest('hex')
            Assert.Equal("1d16fd7e96e8a9681f283b8a251822ffdc71e2a5bda0c5267a044874fa21b82b", HeroSignalClient.IdentityHash("secret", "user-1"));
        }
    }
}
