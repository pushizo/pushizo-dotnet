using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HeroSignal.Tests
{
    /// <summary>Gelen istekleri kaydeden ve sıradaki yanıtları dönen sahte HTTP işleyicisi.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responses = new();

        public List<Recorded> Requests { get; } = new();

        public List<TimeSpan> Delays { get; } = new();

        public FakeHandler Json(int status, string body, params (string Name, string Value)[] headers)
        {
            _responses.Enqueue((_, _) =>
            {
                var res = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                foreach (var (n, v) in headers) res.Headers.TryAddWithoutValidation(n, v);
                return Task.FromResult(res);
            });
            return this;
        }

        public FakeHandler Raw(int status, string body, string contentType, params (string Name, string Value)[] headers)
        {
            _responses.Enqueue((_, _) =>
            {
                var res = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
                foreach (var (n, v) in headers) res.Headers.TryAddWithoutValidation(n, v);
                return Task.FromResult(res);
            });
            return this;
        }

        public FakeHandler Throw(Exception e)
        {
            _responses.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(e));
            return this;
        }

        public FakeHandler Hang()
        {
            _responses.Enqueue(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                throw new InvalidOperationException("ulaşılamaz");
            });
            return this;
        }

        public HeroSignalClient Client(Action<HeroSignalClientOptions>? configure = null)
        {
            var options = new HeroSignalClientOptions
            {
                ApiKey = "hs_live_test",
                AppId = "app_default",
                BaseUrl = "https://api.test",
                HttpClient = new HttpClient(this),
            };
            configure?.Invoke(options);
            return new HeroSignalClient(options)
            {
                Delay = (d, _) =>
                {
                    Delays.Add(d);
                    return Task.CompletedTask;
                },
                Clock = () => new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            if (request.Content != null)
            {
                foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            }
            Requests.Add(new Recorded(request.Method.Method, request.RequestUri!, headers, body));
            if (_responses.Count == 0) throw new InvalidOperationException($"beklenmeyen istek: {request.Method} {request.RequestUri}");
            return await _responses.Dequeue()(request, cancellationToken).ConfigureAwait(false);
        }

        internal sealed record Recorded(string Method, Uri Uri, Dictionary<string, string> Headers, byte[] Body)
        {
            public string BodyText => Encoding.UTF8.GetString(Body);

            public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

            public Dictionary<string, List<string>> Query()
            {
                var result = new Dictionary<string, List<string>>();
                var q = Uri.Query.TrimStart('?');
                if (q.Length == 0) return result;
                foreach (var pair in q.Split('&'))
                {
                    var idx = pair.IndexOf('=');
                    var k = Uri.UnescapeDataString(idx < 0 ? pair : pair.Substring(0, idx));
                    var v = idx < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(idx + 1));
                    if (!result.TryGetValue(k, out var list)) result[k] = list = new List<string>();
                    list.Add(v);
                }
                return result;
            }
        }
    }
}
