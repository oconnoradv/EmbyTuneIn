using MediaBrowser.Common.Net;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInRefreshTimings
    {
        internal TimeSpan PacerQueue;
        internal TimeSpan Pacing;
        internal TimeSpan Backoff;
        internal TimeSpan Fetch;
        internal TimeSpan CacheReplay;
        internal int? MinIntervalSeconds;
        internal int? MaxIntervalSeconds;

        internal void RecordInterval(int seconds)
        {
            MinIntervalSeconds = Math.Min(MinIntervalSeconds ?? seconds, seconds);
            MaxIntervalSeconds = Math.Max(MaxIntervalSeconds ?? seconds, seconds);
        }
    }

    internal sealed class TuneInRequestPacer
    {
        private const int SuccessesBeforeRecovery = 20;
        private readonly SemaphoreSlim owner = new SemaphoreSlim(1, 1);
        private readonly Func<DateTime> clock;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private DateTime nextRequestUtc;
        private DateTime retryNotBeforeUtc;
        private DateTime lastRequestUtc;
        private int catalogueIntervalSeconds;
        private int consecutiveCatalogueRateLimits;
        private int consecutiveCatalogueSuccesses;

        internal TuneInRequestPacer(Func<DateTime> clock = null,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        }

        internal async Task<HttpResponseInfo> SendAsync(Func<Task<HttpResponseInfo>> send,
            int intervalSeconds, CancellationToken token, bool catalogueRequest = false, TuneInRefreshTimings timings = null)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            var elapsed = timings == null ? null : Stopwatch.StartNew();
            try { await owner.WaitAsync(token).ConfigureAwait(false); }
            finally { if (timings != null) timings.PacerQueue += elapsed.Elapsed; }
            try
            {
                var interval = TimeSpan.FromSeconds(Math.Min(PluginConfiguration.MaxApiRequestIntervalSeconds,
                    Math.Max(PluginConfiguration.MinApiRequestIntervalSeconds,
                        Math.Max(intervalSeconds, catalogueRequest ? catalogueIntervalSeconds : 0))));
                var earliest = lastRequestUtc == default(DateTime) ? clock() : lastRequestUtc + interval;
                if (earliest > nextRequestUtc) nextRequestUtc = earliest;
                elapsed?.Restart();
                try { await WaitUntilAsync(nextRequestUtc, token).ConfigureAwait(false); }
                finally { if (timings != null) timings.Pacing += elapsed.Elapsed; }
                elapsed?.Restart();
                try { await WaitUntilAsync(retryNotBeforeUtc, token).ConfigureAwait(false); }
                finally { if (timings != null) timings.Backoff += elapsed.Elapsed; }

                token.ThrowIfCancellationRequested();
                timings?.RecordInterval((int)interval.TotalSeconds);
                lastRequestUtc = clock();
                nextRequestUtc = lastRequestUtc + interval;
                var previousSuccesses = consecutiveCatalogueSuccesses;
                if (catalogueRequest) consecutiveCatalogueSuccesses = 0;
                var response = await send().ConfigureAwait(false);
                if (response == null) return null;
                if ((int)response.StatusCode == 429 || (int)response.StatusCode == 503)
                {
                    var retryAfter = response.Headers?.FirstOrDefault(pair =>
                        pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value;
                    var now = clock();
                    var retryAt = RetryAt(retryAfter, now);
                    if (catalogueRequest && (int)response.StatusCode == 429)
                    {
                        catalogueIntervalSeconds = Math.Min(PluginConfiguration.MaxApiRequestIntervalSeconds,
                            (int)interval.TotalSeconds * 2);
                        consecutiveCatalogueRateLimits = Math.Min(3, consecutiveCatalogueRateLimits + 1);
                        var cooldown = now.AddSeconds(30 * (1 << (consecutiveCatalogueRateLimits - 1)));
                        if (!retryAt.HasValue || retryAt.Value < cooldown) retryAt = cooldown;
                    }
                    if (retryAt.HasValue && retryAt.Value > retryNotBeforeUtc)
                        retryNotBeforeUtc = retryAt.Value;
                }
                else if (catalogueRequest && (int)response.StatusCode == 200)
                {
                    consecutiveCatalogueRateLimits = 0;
                    if (catalogueIntervalSeconds > intervalSeconds)
                    {
                        consecutiveCatalogueSuccesses = previousSuccesses + 1;
                        if (consecutiveCatalogueSuccesses >= SuccessesBeforeRecovery)
                        {
                            catalogueIntervalSeconds--;
                            consecutiveCatalogueSuccesses = 0;
                            nextRequestUtc = lastRequestUtc.AddSeconds(Math.Max(PluginConfiguration.MinApiRequestIntervalSeconds,
                                Math.Max(intervalSeconds, catalogueIntervalSeconds)));
                        }
                    }
                }
                return response;
            }
            finally { owner.Release(); }
        }

        private async Task WaitUntilAsync(DateTime deadline, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = deadline - clock();
                if (remaining <= TimeSpan.Zero) return;
                await delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, token)
                    .ConfigureAwait(false);
            }
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
