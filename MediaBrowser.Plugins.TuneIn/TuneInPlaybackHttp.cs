using MediaBrowser.Common.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class TuneInPlaybackHttp
    {
        // Some stream hosts reject requests without a User-Agent (for example with HTTP 401).
        internal static readonly string UserAgent = "EmbyTuneIn/"
            + typeof(TuneInPlaybackHttp).Assembly.GetName().Version.ToString(3);
        private const int MaxIcyHeaderBytes = 16 * 1024;

        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false
        }) { Timeout = Timeout.InfiniteTimeSpan };

        internal static Task<HttpResponseInfo> SendAsync(HttpRequestOptions options) =>
            SendAsync(options, TuneInPlayback.IsPublicAddress);

        internal static async Task<HttpResponseInfo> SendAsync(HttpRequestOptions options, Func<IPAddress, bool> allowAddress)
        {
            try { return await SendHttpAsync(options).ConfigureAwait(false); }
            catch (HttpRequestException) when (!options.CancellationToken.IsCancellationRequested
                && Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                // Legacy SHOUTcast servers answer "ICY 200 OK", which HttpClient rejects as an invalid status line.
                return await SendIcyAsync(uri, allowAddress, options.CancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<HttpResponseInfo> SendHttpAsync(HttpRequestOptions options)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, options.Url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    options.CancellationToken).ConfigureAwait(false);
                try
                {
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (response.Headers.TryGetValues("Location", out var locations))
                        headers["Location"] = string.Join("", locations);
                    if (response.Headers.TryGetValues("Retry-After", out var retry))
                        headers["Retry-After"] = string.Join("", retry);
                    return new HttpResponseInfo
                    {
                        StatusCode = response.StatusCode,
                        ContentType = response.Content.Headers.ContentType?.MediaType,
                        Headers = headers,
                        Content = new ResponseStream(await response.Content.ReadAsStreamAsync().ConfigureAwait(false), response)
                    };
                }
                catch { response.Dispose(); throw; }
            }
        }

        private static async Task<HttpResponseInfo> SendIcyAsync(Uri uri, Func<IPAddress, bool> allowAddress,
            CancellationToken token)
        {
            var host = uri.IdnHost.Trim('[', ']');
            var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var targets = addresses.Where(allowAddress).ToArray();
            if (targets.Length == 0) throw new HttpRequestException("The stream host has no permitted address.");
            var client = new TcpClient(targets[0].AddressFamily);
            try
            {
                var registration = token.Register(client.Dispose);
                try
                {
                    await client.ConnectAsync(targets.Where(address => address.AddressFamily == targets[0].AddressFamily)
                        .ToArray(), uri.Port).ConfigureAwait(false);
                    var stream = client.GetStream();
                    var request = Encoding.ASCII.GetBytes("GET " + uri.PathAndQuery + " HTTP/1.0\r\n"
                        + "Host: " + uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port) + "\r\n"
                        + "User-Agent: " + UserAgent + "\r\nAccept: */*\r\nIcy-MetaData: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(request, 0, request.Length, token).ConfigureAwait(false);
                    var buffer = new byte[MaxIcyHeaderBytes];
                    int length = 0, end;
                    while ((end = HeaderEnd(buffer, length)) < 0)
                    {
                        if (length == buffer.Length) throw new HttpRequestException("The stream response headers are too large.");
                        var count = await stream.ReadAsync(buffer, length, buffer.Length - length, token).ConfigureAwait(false);
                        if (count == 0) throw new HttpRequestException("The stream closed before sending its headers.");
                        length += count;
                    }
                    token.ThrowIfCancellationRequested();
                    var response = ParseIcyHeaders(Encoding.ASCII.GetString(buffer, 0, end));
                    var body = new byte[length - end];
                    Buffer.BlockCopy(buffer, end, body, 0, body.Length);
                    response.Content = new IcyStream(body, stream, client);
                    return response;
                }
                finally { registration.Dispose(); }
            }
            catch (Exception error)
            {
                client.Dispose();
                token.ThrowIfCancellationRequested();
                if (error is SocketException || error is IOException || error is ObjectDisposedException)
                    throw new HttpRequestException("The stream connection failed.", error);
                throw;
            }
        }

        private static int HeaderEnd(byte[] buffer, int length)
        {
            for (var index = 0; index < length; index++)
            {
                if (buffer[index] != '\n') continue;
                if (index + 1 < length && buffer[index + 1] == '\n') return index + 2;
                if (index + 2 < length && buffer[index + 1] == '\r' && buffer[index + 2] == '\n') return index + 3;
            }
            return -1;
        }

        internal static HttpResponseInfo ParseIcyHeaders(string text)
        {
            var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            var status = lines[0].Split(' ');
            if (status.Length < 2 || !(status[0] == "ICY" || status[0].StartsWith("HTTP/1.", StringComparison.Ordinal))
                || status[1].Length != 3 || !int.TryParse(status[1], out var code) || code < 100)
                throw new HttpRequestException("The stream returned an invalid status line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string contentType = null;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    contentType = value.Split(';')[0].Trim();
                else if (name.Equals("Location", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                    headers[name] = value;
            }
            return new HttpResponseInfo { StatusCode = (HttpStatusCode)code, ContentType = contentType, Headers = headers };
        }

        private sealed class ResponseStream : RadioReadStream
        {
            private readonly Stream stream;
            private HttpResponseMessage response;

            internal ResponseStream(Stream stream, HttpResponseMessage response)
            { this.stream = stream; this.response = response; }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                stream.ReadAsync(buffer, offset, count, token);

            protected override void Dispose(bool disposing)
            {
                if (disposing) Interlocked.Exchange(ref response, null)?.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class IcyStream : RadioReadStream
        {
            private readonly byte[] prefix;
            private readonly Stream stream;
            private TcpClient client;
            private int position;

            internal IcyStream(byte[] prefix, Stream stream, TcpClient client)
            { this.prefix = prefix; this.stream = stream; this.client = client; }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (position < prefix.Length && count > 0)
                {
                    var size = Math.Min(count, prefix.Length - position);
                    Buffer.BlockCopy(prefix, position, buffer, offset, size);
                    position += size;
                    return Task.FromResult(size);
                }
                return stream.ReadAsync(buffer, offset, count, token);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Interlocked.Exchange(ref client, null)?.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
