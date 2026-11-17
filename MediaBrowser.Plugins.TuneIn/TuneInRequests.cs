using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class TuneInRequests
    {
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim Throttle = new SemaphoreSlim(1, 1);
        private static DateTime lastRequest;
        private static TuneInDirectory directory;

        internal static TuneInDirectory Directory(IHttpClient client, IApplicationHost host)
        {
            lock (Gate)
                return directory ?? (directory = new TuneInDirectory(() => Plugin.Instance.Configuration, async (request, token) =>
                {
                    var outbound = request.Copy();
                    outbound.Parameters["partnerid"] = "uD1X52pA";
                    outbound.Parameters["formats"] = "mp3,aac";
                    outbound.Parameters["serial"] = host.SystemId;
                    using (var response = await SendAsync(client, new HttpRequestOptions
                    { Url = outbound.Url, CancellationToken = token }, "GET").ConfigureAwait(false))
                    using (var memory = new MemoryStream())
                    {
                        var buffer = new byte[8192];
                        int count;
                        while ((count = await response.Content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                        {
                            if (memory.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("TuneIn catalogue exceeds limit.");
                            memory.Write(buffer, 0, count);
                        }
                        return TuneInDirectory.ParseCatalogue(memory.ToArray());
                    }
                }));
        }

        internal static async Task<HttpResponseInfo> SendAsync(IHttpClient client, HttpRequestOptions options, string method)
        {
            await Throttle.WaitAsync(options.CancellationToken).ConfigureAwait(false);
            try
            {
                var delay = lastRequest.AddMilliseconds(3000) - DateTime.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, options.CancellationToken).ConfigureAwait(false);
                lastRequest = DateTime.UtcNow;
                options.BufferContent = true;
                return await client.SendAsync(options, method).ConfigureAwait(false);
            }
            finally { Throttle.Release(); }
        }
    }
}
