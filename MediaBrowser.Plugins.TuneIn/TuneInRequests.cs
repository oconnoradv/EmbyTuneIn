using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

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
        internal static readonly TuneInRequestPacer Pacer = new TuneInRequestPacer();
        private static TuneInDirectory directory;

        internal static TuneInDirectory Directory(IHttpClient client, IApplicationHost host)
        {
            lock (Gate)
                return directory ?? (directory = new TuneInDirectory(() => Plugin.Instance.Configuration, async (request, token, timings) =>
                {
                    var outbound = request.Copy();
                    outbound.Parameters["partnerid"] = "uD1X52pA";
                    outbound.Parameters["formats"] = "mp3,aac";
                    outbound.Parameters["serial"] = host.SystemId;
                    return await ReadCatalogAsync(Pacer, (operationToken) => client.SendAsync(new HttpRequestOptions
                    {
                        Url = outbound.Url, CancellationToken = operationToken, ThrowOnErrorResponse = false, BufferContent = false
                    }, "GET"), Plugin.Instance.Configuration.ApiRequestIntervalSeconds, token,
                        TimeSpan.FromSeconds(90), timings).ConfigureAwait(false);
                }));
        }

        internal static async Task<XDocument> ReadCatalogAsync(TuneInRequestPacer pacer,
            Func<CancellationToken, Task<HttpResponseInfo>> send, int interval, CancellationToken token,
            TimeSpan responseTimeout, TuneInRefreshTimings timings = null)
        {
            using (var deadline = new CancellationTokenSource())
            using (var operation = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            using (var memory = new MemoryStream())
            {
                var started = false;
                var expired = false;
                var registration = deadline.Token.Register(() =>
                {
                    if (timings == null) return;
                    expired = true;
                    Interlocked.Increment(ref timings.ExpiredResponses);
                });
                try
                {
                    // This timer excludes queue, pacing and Retry-After waits.
                    using (var response = await pacer.SendAsync(async () =>
                    {
                        started = true;
                        if (timings != null) Interlocked.Increment(ref timings.ActiveResponses);
                        deadline.CancelAfter(responseTimeout);
                        var result = await send(operation.Token).ConfigureAwait(false);
                        try
                        {
                            if ((int)result.StatusCode != 200) return result;
                            var buffer = new byte[8192];
                            int count;
                            while ((count = await result.Content.ReadAsync(buffer, 0, buffer.Length, operation.Token).ConfigureAwait(false)) != 0)
                            {
                                operation.Token.ThrowIfCancellationRequested();
                                if (memory.Length + count > 2 * 1024 * 1024)
                                    throw new InvalidDataException("TuneIn catalog exceeds limit.");
                                memory.Write(buffer, 0, count);
                            }
                            operation.Token.ThrowIfCancellationRequested();
                            return result;
                        }
                        catch
                        {
                            result.Dispose();
                            throw;
                        }
                    }, interval, token, catalogueRequest: true, timings: timings).ConfigureAwait(false))
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        if ((int)response.StatusCode != 200)
                            throw new TuneInProviderResponseException((int)response.StatusCode);
                        return TuneInDirectory.ParseCatalogue(memory.ToArray());
                    }
                }
                catch (Exception error) when (deadline.IsCancellationRequested && !token.IsCancellationRequested
                    && (error is OperationCanceledException || error is IOException || error is TimeoutException))
                {
                    throw new TuneInCatalogTimeoutException();
                }
                finally
                {
                    registration.Dispose();
                    if (expired) Interlocked.Decrement(ref timings.ExpiredResponses);
                    if (started && timings != null) Interlocked.Decrement(ref timings.ActiveResponses);
                }
            }
        }

    }

    internal sealed class TuneInCatalogTimeoutException : TimeoutException
    {
        internal TuneInCatalogTimeoutException() : base("TuneIn catalog response deadline reached.") { }
    }
}
