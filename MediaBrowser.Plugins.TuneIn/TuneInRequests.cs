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
        private static readonly TuneInRequestPacer Pacer = new TuneInRequestPacer();
        private static TuneInDirectory directory;
        private static TuneInRefresh refresh;

        internal static TuneInRefresh Refresh(IHttpClient client, IApplicationHost host)
        {
            lock (Gate) return refresh ?? (refresh = new TuneInRefresh(Directory(client, host),
                new TuneInRefreshStore(Plugin.Instance.RefreshStatePath)));
        }

        internal static void UpdateConfiguration(Action update)
        {
            lock (Gate)
            {
                if (refresh == null) update();
                else refresh.UpdateConfiguration(update);
            }
        }

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
                        directory.ReportHttpResponse((int)response.StatusCode);
                        if ((int)response.StatusCode != 200)
                            throw new InvalidDataException("TuneIn catalogue returned HTTP " + (int)response.StatusCode + ".");
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

        internal static Task<HttpResponseInfo> SendAsync(IHttpClient client, HttpRequestOptions options, string method)
        {
            options.BufferContent = true;
            return Pacer.SendAsync(() => client.SendAsync(options, method),
                Plugin.Instance?.Configuration.ApiRequestIntervalSeconds ?? 3, options.CancellationToken);
        }
    }
}
