using MediaBrowser.Common.Net;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInRequestPacer
    {
        private readonly SemaphoreSlim owner = new SemaphoreSlim(1, 1);
        private readonly Func<DateTime> clock;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private DateTime nextRequest;
        private DateTime lastRequest;

        internal TuneInRequestPacer(Func<DateTime> clock = null, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        }

        internal async Task<HttpResponseInfo> SendAsync(Func<Task<HttpResponseInfo>> send, int intervalSeconds, CancellationToken token)
        {
            await owner.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var earliest = lastRequest.AddSeconds(Math.Max(3, intervalSeconds));
                if (earliest > nextRequest) nextRequest = earliest;
                while (nextRequest > clock())
                    await delay(TimeSpan.FromMilliseconds(Math.Min((nextRequest - clock()).TotalMilliseconds,
                        TimeSpan.FromDays(1).TotalMilliseconds)), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lastRequest = clock();
                nextRequest = lastRequest.AddSeconds(Math.Max(3, intervalSeconds));
                var response = await send().ConfigureAwait(false);
                var retry = response.Headers?.FirstOrDefault(pair =>
                    pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value;
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                {
                    if (long.TryParse(retry, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    {
                        var remaining = (DateTime.MaxValue - clock()).TotalSeconds;
                        var until = clock().AddSeconds(Math.Min(seconds, remaining - 1));
                        if (until > nextRequest) nextRequest = until;
                    }
                    else if (DateTimeOffset.TryParse(retry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until)
                        && until.UtcDateTime > nextRequest) nextRequest = until.UtcDateTime;
                }
                return response;
            }
            finally { owner.Release(); }
        }
    }
}
