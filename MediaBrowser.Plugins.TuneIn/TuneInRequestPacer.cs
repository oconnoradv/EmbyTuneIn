using MediaBrowser.Common.Net;
using MediaBrowser.Plugins.TuneIn.Configuration;
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
        private DateTime nextRequestUtc;
        private DateTime lastRequestUtc;

        internal TuneInRequestPacer(Func<DateTime> clock = null,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        }

        internal async Task<HttpResponseInfo> SendAsync(Func<Task<HttpResponseInfo>> send,
            int intervalSeconds, CancellationToken token, bool catalogueRequest = false)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            await owner.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var interval = TimeSpan.FromSeconds(Math.Min(PluginConfiguration.MaxApiRequestIntervalSeconds,
                    Math.Max(PluginConfiguration.MinApiRequestIntervalSeconds, intervalSeconds)));
                var earliest = lastRequestUtc == default(DateTime) ? clock() : lastRequestUtc + interval;
                if (earliest > nextRequestUtc) nextRequestUtc = earliest;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var remaining = nextRequestUtc - clock();
                    if (remaining <= TimeSpan.Zero) break;
                    await delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, token)
                        .ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                lastRequestUtc = clock();
                nextRequestUtc = lastRequestUtc + interval;
                var response = await send().ConfigureAwait(false);
                if (response == null) return null;
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                {
                    var retryAfter = response.Headers?.FirstOrDefault(pair =>
                        pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value;
                    var retryAt = RetryAt(retryAfter, clock());
                    if (catalogueRequest && (int)response.StatusCode == 429 && !retryAt.HasValue)
                        retryAt = clock().AddSeconds(30);
                    if (retryAt.HasValue && retryAt.Value > nextRequestUtc)
                        nextRequestUtc = retryAt.Value;
                }
                return response;
            }
            finally { owner.Release(); }
        }

        private static DateTime? RetryAt(string value, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                if (seconds < 0) return null;
                var maximum = (DateTime.MaxValue - now).Ticks / TimeSpan.TicksPerSecond;
                return seconds > maximum ? DateTime.MaxValue : now.AddTicks(seconds * TimeSpan.TicksPerSecond);
            }
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                return date.UtcDateTime;
            return null;
        }
    }
}
