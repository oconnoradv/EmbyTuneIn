using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInProviderResponseException : IOException
    {
        internal int StatusCode { get; }
        internal TuneInProviderResponseException(int statusCode)
            : base("TuneIn catalog returned HTTP " + statusCode + ".") { StatusCode = statusCode; }
    }

    internal static class TuneInRequests
    {
        private static readonly object Gate = new object();
        private static readonly TuneInRequestPacer Pacer = new TuneInRequestPacer();
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
                    { Url = outbound.Url, CancellationToken = token, ThrowOnErrorResponse = false }, "GET",
                        Plugin.Instance.Configuration.ApiRequestIntervalSeconds, catalogueRequest: true).ConfigureAwait(false))
                    using (var memory = new MemoryStream())
                    {
                        if ((int)response.StatusCode != 200)
                            throw new TuneInProviderResponseException((int)response.StatusCode);
                        var buffer = new byte[8192];
                        int count;
                        while ((count = await response.Content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                        {
                            if (memory.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("TuneIn catalog exceeds limit.");
                            memory.Write(buffer, 0, count);
                        }
                        return TuneInDirectory.ParseCatalogue(memory.ToArray());
                    }
                }));
        }

        internal static Task<HttpResponseInfo> SendAsync(IHttpClient client, HttpRequestOptions options, string method,
            int intervalSeconds, bool catalogueRequest = false, bool bufferContent = true)
        {
            options.BufferContent = bufferContent;
            return Pacer.SendAsync(() => client.SendAsync(options, method), intervalSeconds,
                options.CancellationToken, catalogueRequest);
        }
    }
}
