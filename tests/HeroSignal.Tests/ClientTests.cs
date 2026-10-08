using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HeroSignal.Models;
using HeroSignal.Resources;
using Xunit;

namespace HeroSignal.Tests
{
    /// <summary>İstemci davranışı: kimlik doğrulama, app kapsamı, idempotency, yeniden deneme, hata eşleme, sayfalama.</summary>
    public class ClientTests
    {
        private static CreateNotificationDto Notification() => new()
        {
            Target = new CreateNotificationDtoTarget { IncludeAliases = new() { ["external_id"] = new() { "u1" } } },
            Contents = new() { ["en"] = "Hi" },
        };

        [Fact]
        public void RequiresApiKey()
        {
            Assert.Throws<ArgumentException>(() => new HeroSignalClient(new HeroSignalClientOptions()));
        }

        [Fact]
        public async Task CreateNotificationSendsHeadersBodyAndReusesIdempotencyKey()
        {
            var fake = new FakeHandler()
                .Json(503, "{\"error\":{\"code\":\"UNAVAILABLE\",\"message\":\"x\"}}")
                .Json(202, "{\"id\":\"msg_1\",\"status\":\"queued\"}");
            using var hs = fake.Client();
            var result = await hs.Notifications.CreateAsync(Notification());
            Assert.Equal("msg_1", result.Id);

            Assert.Equal(2, fake.Requests.Count);
            var r = fake.Requests[0];
            Assert.Equal("POST", r.Method);
            Assert.Equal("https://api.test/v1/notifications", r.Uri.ToString());
            Assert.Equal("Bearer hs_live_test", r.Header("Authorization"));
            Assert.StartsWith("herosignal-dotnet/", r.Header("User-Agent"));
            Assert.StartsWith("application/json", r.Header("Content-Type"));
            using var body = JsonDocument.Parse(r.Body);
            Assert.Equal("app_default", body.RootElement.GetProperty("app_id").GetString());
            Assert.False(body.RootElement.TryGetProperty("headings", out _));

            var k1 = r.Header("Idempotency-Key");
            Assert.True(Guid.TryParse(k1, out _));
            Assert.Equal(k1, fake.Requests[1].Header("Idempotency-Key"));
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(300) }, fake.Delays);
        }

        [Fact]
        public async Task IdempotencyKeyExplicitBodyDisabledAndNonIdempotent()
        {
            var fake = new FakeHandler().Json(202, "{}").Json(202, "{}").Json(202, "{}").Json(201, "{}");
            using var hs = fake.Client();
            await hs.Notifications.CreateAsync(Notification(), new RequestOptions { IdempotencyKey = "order-1" });
            var n = Notification();
            n.IdempotencyKey = "order-2";
            await hs.Notifications.CreateAsync(n);
            using var off = fake.Client(o => o.AutoIdempotencyKey = false);
            await off.Notifications.CreateAsync(Notification());
            await hs.Suppressions.CreateAsync(new CreateSuppressionDto { Channel = "email", Value = "a@b.c" });

            Assert.Equal("order-1", fake.Requests[0].Header("Idempotency-Key"));
            Assert.Equal("order-2", fake.Requests[1].Header("Idempotency-Key"));
            Assert.Null(fake.Requests[2].Header("Idempotency-Key"));
            Assert.Null(fake.Requests[3].Header("Idempotency-Key"));
        }

        [Fact]
        public async Task AppIdPathQueryAndOverride()
        {
            var fake = new FakeHandler()
                .Json(200, "{\"id\":\"seg_1\"}")
                .Json(200, "{\"data\":[],\"has_more\":false}");
            using var hs = fake.Client();
            await hs.Segments.GetAsync("seg/1", new RequestOptions { AppId = "app_other" });
            Assert.Equal("/v1/apps/app_other/segments/seg%2F1", fake.Requests[0].Uri.AbsolutePath);

            await hs.Notifications.ListAsync(new NotificationsListParams { Limit = 10, Status = "completed" });
            var q = fake.Requests[1].Query();
            Assert.Equal("app_default", q["app_id"].Single());
            Assert.Equal("10", q["limit"].Single());
            Assert.Equal("completed", q["status"].Single());
            Assert.False(q.ContainsKey("cursor"));
        }

        [Fact]
        public async Task MissingAppIdAndEmptyPathParam()
        {
            var fake = new FakeHandler();
            using var hs = fake.Client(o => o.AppId = null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => hs.Segments.ListAsync());
            using var hs2 = fake.Client();
            await Assert.ThrowsAsync<ArgumentException>(() => hs2.Segments.GetAsync(""));
            Assert.Empty(fake.Requests);
        }

        [Fact]
        public async Task RetryBackoffAndRetryAfter()
        {
            var fake = new FakeHandler()
                .Json(500, "{}")
                .Json(429, "{}", ("Retry-After", "2"))
                .Json(502, "{}")
                .Json(200, "{\"data\":[]}");
            using var hs = fake.Client();
            await hs.Apps.ListAsync();
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3) }, fake.Delays);
        }

        [Fact]
        public async Task RetryAfterHttpDate()
        {
            var fake = new FakeHandler()
                .Json(429, "{}", ("Retry-After", "Tue, 01 Jan 2030 00:00:10 GMT"))
                .Json(200, "{}");
            using var hs = fake.Client();
            await hs.Apps.ListAsync();
            Assert.Equal(new[] { TimeSpan.FromSeconds(10) }, fake.Delays);
        }

        [Fact]
        public async Task RetriesExhausted()
        {
            var fake = new FakeHandler()
                .Json(503, "{\"error\":{\"code\":\"UNAVAILABLE\",\"message\":\"down\",\"request_id\":\"req_1\"}}")
                .Json(503, "{\"error\":{\"code\":\"UNAVAILABLE\",\"message\":\"down\",\"request_id\":\"req_2\"}}");
            using var hs = fake.Client(o => o.MaxRetries = 1);
            var ex = await Assert.ThrowsAsync<HeroSignalException>(() => hs.Apps.ListAsync());
            Assert.Equal(503, ex.Status);
            Assert.Equal("UNAVAILABLE", ex.Code);
            Assert.Equal("req_2", ex.RequestId);
            Assert.True(ex.IsRetryable);
            Assert.Equal(2, fake.Requests.Count);
            Assert.Single(fake.Delays);
        }

        [Fact]
        public async Task ClientErrorMappedAndNotRetried()
        {
            var fake = new FakeHandler()
                .Json(422, "{\"error\":{\"code\":\"VALIDATION_FAILED\",\"message\":\"bad\",\"request_id\":\"req_1\",\"details\":{\"field\":\"x\"}}}")
                .Raw(404, "not found", "text/plain", ("X-Request-Id", "req_h"));
            using var hs = fake.Client();
            var ex = await Assert.ThrowsAsync<HeroSignalException>(() => hs.Apps.ListAsync());
            Assert.Equal(422, ex.Status);
            Assert.Equal("VALIDATION_FAILED", ex.Code);
            Assert.Equal("bad", ex.Message);
            Assert.Equal("req_1", ex.RequestId);
            Assert.Equal("x", ex.Details!["field"].GetString());
            Assert.False(ex.IsRetryable);
            Assert.Contains("req_1", ex.ToString());

            var ex2 = await Assert.ThrowsAsync<HeroSignalException>(() => hs.Apps.GetAsync());
            Assert.Equal("HTTP_404", ex2.Code);
            Assert.Equal("req_h", ex2.RequestId);
            Assert.Equal(2, fake.Requests.Count);
        }

        [Fact]
        public async Task NetworkErrorRetriedThenSucceeds()
        {
            var fake = new FakeHandler()
                .Throw(new HttpRequestException("boom"))
                .Hang()
                .Json(200, "{\"data\":[]}");
            using var hs = fake.Client(o => o.Timeout = TimeSpan.FromMilliseconds(100));
            await hs.Apps.ListAsync();
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1) }, fake.Delays);
        }

        [Fact]
        public async Task NetworkErrorExhausted()
        {
            var fake = new FakeHandler().Throw(new HttpRequestException("boom"));
            using var hs = fake.Client(o => o.MaxRetries = 0);
            var ex = await Assert.ThrowsAsync<HeroSignalException>(() => hs.Apps.ListAsync());
            Assert.Equal(0, ex.Status);
            Assert.Equal("NETWORK", ex.Code);
            Assert.True(ex.IsRetryable);
            Assert.IsType<HttpRequestException>(ex.InnerException);
        }

        [Fact]
        public async Task CallerCancellationIsNotRetried()
        {
            var fake = new FakeHandler().Hang();
            using var hs = fake.Client();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hs.Apps.ListAsync(null, cts.Token));
            Assert.Empty(fake.Delays);
        }

        [Fact]
        public async Task RequestOptionsOverrideRetriesAndHeaders()
        {
            var fake = new FakeHandler().Json(500, "{}");
            using var hs = fake.Client();
            var options = new RequestOptions { MaxRetries = 0 };
            options.Headers["X-Trace"] = "t1";
            await Assert.ThrowsAsync<HeroSignalException>(() => hs.Apps.ListAsync(options));
            Assert.Single(fake.Requests);
            Assert.Equal("t1", fake.Requests[0].Header("X-Trace"));
        }

        [Fact]
        public async Task AutoPaging()
        {
            var fake = new FakeHandler()
                .Json(200, "{\"data\":[{\"id\":\"s1\"},{\"id\":\"s2\"}],\"next_cursor\":\"c2\",\"has_more\":true}")
                .Json(200, "{\"data\":[{\"id\":\"s3\"}],\"next_cursor\":null,\"has_more\":false}");
            using var hs = fake.Client();
            var ids = new List<string?>();
            await foreach (var s in hs.Segments.ListAutoPagingAsync(new SegmentsListParams { Limit = 2 }))
            {
                ids.Add(s.Id);
            }
            Assert.Equal(new[] { "s1", "s2", "s3" }, ids);
            Assert.False(fake.Requests[0].Query().ContainsKey("cursor"));
            Assert.Equal("c2", fake.Requests[1].Query()["cursor"].Single());
            Assert.Equal("2", fake.Requests[1].Query()["limit"].Single());
        }

        [Fact]
        public async Task MultipartUpload()
        {
            var fake = new FakeHandler().Json(201, "{\"id\":\"imp_1\"}");
            using var hs = fake.Client();
            await hs.Imports.CreateAsync(new ImportsCreateParams
            {
                Type = "csv_users",
                File = new FileUpload("users.csv", Encoding.UTF8.GetBytes("external_id\nu1\n"), "text/csv"),
            });
            var r = fake.Requests[0];
            Assert.Equal("/v1/apps/app_default/imports", r.Uri.AbsolutePath);
            Assert.StartsWith("multipart/form-data", r.Header("Content-Type"));
            Assert.Contains("name=type", r.BodyText);
            Assert.Contains("csv_users", r.BodyText);
            Assert.Contains("filename=users.csv", r.BodyText);
            Assert.Contains("external_id", r.BodyText);
        }

        [Fact]
        public async Task RawRequestAndQueryEncoding()
        {
            var fake = new FakeHandler()
                .Raw(200, "a,b\n1,2\n", "text/csv")
                .Json(200, "{\"ok\":true}");
            using var hs = fake.Client();
            var csv = await hs.RequestAsync("GET", "/v1/custom", new Dictionary<string, object?>
            {
                ["format"] = "csv",
                ["b"] = new[] { 1, 2 },
                ["flag"] = true,
                ["skip"] = null,
                ["obj"] = new Dictionary<string, int> { ["x"] = 1 },
            });
            Assert.Equal("a,b\n1,2\n", csv!.Value.GetString());
            var q = fake.Requests[0].Query();
            Assert.Equal(new[] { "1", "2" }, q["b"]);
            Assert.Equal("true", q["flag"].Single());
            Assert.Equal("{\"x\":1}", q["obj"].Single());
            Assert.False(q.ContainsKey("skip"));

            var json = await hs.RequestAsync("GET", "/v1/other");
            Assert.True(json!.Value.GetProperty("ok").GetBoolean());
        }

        [Fact]
        public void IdentityHashMatchesHmac()
        {
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("user-1"))).ToLowerInvariant();
            Assert.Equal(expected, HeroSignalClient.IdentityHash("secret", "user-1"));
        }
    }
}
