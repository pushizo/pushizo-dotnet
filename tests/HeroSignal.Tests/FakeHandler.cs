using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HeroSignal.Tests
{
    /// <summary>Sıralı yanıtlar döndüren ve istekleri (gövdeleriyle) kaydeden sahte HTTP işleyicisi.</summary>
    internal sealed class FakeHandler : HttpMessageHandler
    {
        internal sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, byte[] Body, string? ContentType);

        private readonly Queue<Func<HttpResponseMessage>> _responses = new Queue<Func<HttpResponseMessage>>();
        public List<Recorded> Requests { get; } = new List<Recorded>();

        public FakeHandler Add(int status, string body, params (string Name, string Value)[] headers)
        {
            _responses.Enqueue(() =>
            {
                var res = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                foreach (var (name, value) in headers)
                {
                    res.Headers.TryAddWithoutValidation(name, value);
                }
                return res;
            });
            return this;
        }

        public FakeHandler NetworkError()
        {
            _responses.Enqueue(() => throw new HttpRequestException("bağlantı koptu"));
            return this;
        }

        /// <summary>Bağlantı reddi: istek sunucuya hiç gitmedi.</summary>
        public FakeHandler ConnectionRefused()
        {
            _responses.Enqueue(() => throw new HttpRequestException(HttpRequestError.ConnectionError, "bağlantı reddedildi",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method, request.RequestUri!, headers, body, request.Content?.Headers.ContentType?.ToString()));
            if (_responses.Count == 0)
            {
                throw new HttpRequestException("yanıt yok");
            }
            return _responses.Dequeue()();
        }
    }
}
