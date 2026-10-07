using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HeroSignal.Models;
using HeroSignal.Params;
using Xunit;

namespace HeroSignal.Tests
{
    public class ClientTests
    {
        private readonly List<TimeSpan> _delays = new List<TimeSpan>();

        private HeroSignalClient Client(FakeHandler handler, bool autoIdempotency = true, int maxRetries = 3)
        {
            var client = new HeroSignalClient(new HeroSignalOptions
            {
                ApiKey = "hs_live_test",
                AppId = "app_1",
                BaseUrl = "https://api.test/",
                HttpClient = new HttpClient(handler),
                AutoIdempotencyKey = autoIdempotency,
                MaxRetries = maxRetries,
            });
            client.Delay = (d, ct) =>
            {
                _delays.Add(d);
                return Task.CompletedTask;
            };
            return client;
        }

        private static JsonElement Body(FakeHandler h, int i) => JsonDocument.Parse(h.Requests[i].Body).RootElement;

        [Fact]
        public async Task CreateNotificationSendsAuthAppAndIdempotencyKey()
        {
            var h = new FakeHandler().Add(202, "{\"id\":\"msg_1\",\"status\":\"queued\"}");
            var res = await Client(h).Notifications.CreateAsync(new CreateNotificationDto
            {
                Target = new CreateNotificationDtoTarget { IncludeAliases = new Dictionary<string, List<string>> { ["external_id"] = new List<string> { "u1" } } },
                Contents = new Dictionary<string, string> { ["tr"] = "Merhaba" },
            });
            Assert.Equal("msg_1", res.Id);
            var r = h.Requests[0];
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("https://api.test/v1/notifications", r.Uri.ToString());
            Assert.Equal("Bearer hs_live_test", r.Headers["Authorization"]);
            Assert.StartsWith("herosignal-dotnet/", r.Headers["User-Agent"]);
            Assert.Matches(new Regex("^[0-9a-f-]{36}$"), r.Headers["Idempotency-Key"]);
            var body = Body(h, 0);
            Assert.Equal("app_1", body.GetProperty("app_id").GetString());
            Assert.Equal("Merhaba", body.GetProperty("contents").GetProperty("tr").GetString());
            Assert.False(body.TryGetProperty("idempotency_key", out _), "null alanlar gönderilmez");
        }

        [Fact]
        public async Task ExplicitAndBodyIdempotencyKeyAndPerCallApp()
        {
            var h = new FakeHandler().Add(202, "{\"id\":\"m\"}").Add(202, "{\"id\":\"m\"}");
            var hs = Client(h);
            var dto = new CreateNotificationDto { Contents = new Dictionary<string, string> { ["en"] = "x" } };
            await hs.Notifications.CreateAsync(dto, new RequestOptions { IdempotencyKey = "order-1" });
            Assert.Null(dto.AppId);
            await hs.Notifications.CreateAsync(new CreateNotificationDto { Contents = new Dictionary<string, string> { ["en"] = "x" }, IdempotencyKey = "order-2" }, RequestOptions.ForApp("app_2"));
            Assert.Equal("order-1", h.Requests[0].Headers["Idempotency-Key"]);
            Assert.Equal("order-2", h.Requests[1].Headers["Idempotency-Key"]);
            Assert.Equal("app_2", Body(h, 1).GetProperty("app_id").GetString());
        }

        [Fact]
        public async Task NoIdempotencyKeyWhenDisabledOrUnsupported()
        {
            var h = new FakeHandler().Add(202, "{\"id\":\"m\"}").Add(200, "{\"accepted\":1}");
            var hs = Client(h, autoIdempotency: false);
            await hs.Notifications.CreateAsync(new CreateNotificationDto { Contents = new Dictionary<string, string> { ["en"] = "x" } });
            await hs.Events.SendAsync(new ApiEventsDto { Events = new List<ApiEventsDtoEventsItem> { new ApiEventsDtoEventsItem { Name = "purchase" } } });
            Assert.False(h.Requests[0].Headers.ContainsKey("Idempotency-Key"));
            Assert.False(h.Requests[1].Headers.ContainsKey("Idempotency-Key"));
        }

        [Fact]
        public async Task QueryAndPathEncoding()
        {
            var h = new FakeHandler().Add(200, "{\"data\":[]}").Add(200, "{\"id\":\"usr_1\"}");
            var hs = Client(h);
            await hs.Notifications.ListAsync(new NotificationsListParams { Limit = 10, Status = "completed" });
            Assert.Equal("https://api.test/v1/notifications?app_id=app_1&limit=10&status=completed", h.Requests[0].Uri.AbsoluteUri);
            var user = await hs.Users.GetByAliasAsync("crm id", "a/b", RequestOptions.ForApp("app 2"));
            Assert.Equal("/v1/apps/app%202/users/by/crm%20id/a%2Fb", h.Requests[1].Uri.AbsolutePath);
            Assert.Equal("usr_1", user.Id);
        }

        [Fact]
        public async Task MissingAppIdThrows()
        {
            var hs = new HeroSignalClient(new HeroSignalOptions { ApiKey = "k", HttpClient = new HttpClient(new FakeHandler()) });
            await Assert.ThrowsAsync<InvalidOperationException>(() => hs.Apps.GetAsync());
            Assert.Throws<ArgumentException>(() => new HeroSignalClient(new HeroSignalOptions()));
        }

        [Fact]
        public async Task RetryAfterBackoffAndStableIdempotencyKey()
        {
            var h = new FakeHandler()
                .Add(429, "{\"error\":{\"code\":\"HS_RATE_LIMITED\",\"message\":\"yavaş\"}}", ("Retry-After", "2"))
                .Add(503, "{\"error\":{\"code\":\"HS_SERVICE_UNAVAILABLE\",\"message\":\"x\"}}")
                .NetworkError()
                .Add(202, "{\"id\":\"msg_1\"}");
            var res = await Client(h).Notifications.CreateAsync(new CreateNotificationDto { Contents = new Dictionary<string, string> { ["en"] = "x" } });
            Assert.Equal("msg_1", res.Id);
            Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, _delays);
            Assert.Single(h.Requests.Select(r => r.Headers["Idempotency-Key"]).Distinct());
        }

        [Fact]
        public async Task TypedErrorsAndNoRetryOn4xx()
        {
            var h = new FakeHandler()
                .Add(404, "{\"error\":{\"code\":\"HS_NOT_FOUND\",\"message\":\"yok\",\"request_id\":\"req_1\",\"details\":{\"id\":\"x\"}}}")
                .Add(422, "{\"error\":{\"code\":\"HS_NO_RECIPIENTS\",\"message\":\"boş\"}}")
                .Add(401, "{\"error\":{\"code\":\"HS_UNAUTHENTICATED\",\"message\":\"anahtar\"}}");
            var hs = Client(h);
            var nf = await Assert.ThrowsAsync<NotFoundException>(() => hs.Notifications.GetAsync("msg_x"));
            Assert.Equal(404, nf.Status);
            Assert.Equal(ErrorCodes.NotFound, nf.Code);
            Assert.Equal("req_1", nf.RequestId);
            Assert.Equal("x", nf.Details!["id"]);
            Assert.False(nf.IsRetryable);
            var ir = await Assert.ThrowsAsync<InvalidRequestException>(() => hs.Segments.GetAsync("seg_1"));
            Assert.Equal(ErrorCodes.NoRecipients, ir.Code);
            await Assert.ThrowsAsync<AuthenticationException>(() => hs.Apps.ListAsync());
            Assert.Empty(_delays);
            Assert.Equal(3, h.Requests.Count);
        }

        [Fact]
        public async Task RetriesExhausted()
        {
            var h = new FakeHandler().Add(500, "{}").Add(500, "{}").Add(500, "{}").Add(500, "{}");
            var e = await Assert.ThrowsAsync<ServerException>(() => Client(h).Apps.ListAsync());
            Assert.Equal("HTTP_500", e.Code);
            Assert.Equal(4, h.Requests.Count);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, _delays);

            var h2 = new FakeHandler().NetworkError().NetworkError();
            var ne = await Assert.ThrowsAsync<NetworkException>(() => Client(h2, maxRetries: 1).Apps.ListAsync());
            Assert.Equal(0, ne.Status);
            Assert.Equal(ErrorCodes.Network, ne.Code);

            var h3 = new FakeHandler().Add(429, "{}");
            await Assert.ThrowsAsync<RateLimitException>(() => Client(h3).Apps.ListAsync(new RequestOptions { MaxRetries = 0 }));
        }

        [Fact]
        public async Task PaginationIteratesAllPages()
        {
            var h = new FakeHandler()
                .Add(200, "{\"data\":[{\"id\":\"s1\"},{\"id\":\"s2\"}],\"next_cursor\":\"c2\",\"has_more\":true}")
                .Add(200, "{\"data\":[{\"id\":\"s3\"}],\"next_cursor\":null,\"has_more\":false}");
            var ids = new List<string?>();
            var p = new SegmentsListParams { Limit = 2 };
            await foreach (var s in Client(h).Segments.ListAllAsync(p))
            {
                ids.Add(s.Id);
            }
            Assert.Equal(new[] { "s1", "s2", "s3" }, ids);
            Assert.Null(p.Cursor);
            Assert.Equal("https://api.test/v1/apps/app_1/segments?limit=2", h.Requests[0].Uri.AbsoluteUri);
            Assert.Equal("https://api.test/v1/apps/app_1/segments?limit=2&cursor=c2", h.Requests[1].Uri.AbsoluteUri);
        }

        [Fact]
        public async Task MultipartImport()
        {
            var h = new FakeHandler().Add(202, "{\"id\":\"imp_1\"}");
            await Client(h).Imports.CreateAsync(new ImportsCreateForm
            {
                Type = "csv_users",
                File = new FileUpload("users.csv", Encoding.UTF8.GetBytes("external_id\nu1\n"), "text/csv"),
            });
            var r = h.Requests[0];
            Assert.StartsWith("multipart/form-data; boundary=", r.ContentType);
            var body = Encoding.UTF8.GetString(r.Body);
            Assert.Contains("name=\"type\"\r\n\r\ncsv_users", body);
            Assert.Contains("filename=\"users.csv\"", body);
            Assert.Contains("external_id\nu1\n", body);
        }

        [Fact]
        public async Task CancellationIsNotRetried()
        {
            var h = new FakeHandler().Add(200, "{}");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(h).Apps.ListAsync(cancellationToken: cts.Token));
            Assert.Empty(_delays);
        }
    }
}
