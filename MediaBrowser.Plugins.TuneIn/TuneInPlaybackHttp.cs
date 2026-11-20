using MediaBrowser.Common.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class TuneInPlaybackHttp
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false
        }) { Timeout = Timeout.InfiniteTimeSpan };

        internal static async Task<HttpResponseInfo> SendAsync(HttpRequestOptions options)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, options.Url))
            {
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
    }
}
