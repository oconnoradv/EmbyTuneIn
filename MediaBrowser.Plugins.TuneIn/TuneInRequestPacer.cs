using MediaBrowser.Common.Net;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInRefreshTimings
    {
        internal readonly object Gate = new object();
        internal TimeSpan PacerQueue;
        internal TimeSpan Pacing;
        internal TimeSpan Backoff;
        internal TimeSpan Fetch;
        internal TimeSpan CacheReplay;
        internal int? MinIntervalSeconds;
        internal int? MaxIntervalSeconds;
        internal int BackoffWaiters;
        internal int ActiveResponses;
        internal int ExpiredResponses;

        internal string DescribeWait() => Volatile.Read(ref ExpiredResponses) != 0
            ? "TuneIn response deadline reached; waiting for the request to stop"
            : Volatile.Read(ref ActiveResponses) != 0
            ? "Waiting for TuneIn catalog response"
            : Volatile.Read(ref BackoffWaiters) != 0 ? "Waiting for TuneIn retry delay" : null;

        internal void RecordInterval(int seconds)
        {
            MinIntervalSeconds = Math.Min(MinIntervalSeconds ?? seconds, seconds);
            MaxIntervalSeconds = Math.Max(MaxIntervalSeconds ?? seconds, seconds);
        }
    }

    internal sealed class TuneInRequestPacer
    {
        private const int SuccessesBeforeRecovery = 20;
        private readonly object gate = new object();
        private readonly LinkedList<bool> waiting = new LinkedList<bool>();
        private TaskCompletionSource<bool> slotChanged = NewSlotSignal();
        private int active;
        private bool exclusive;
        private bool serialCatalogue;
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
            var parallel = catalogueRequest && timings != null;
            Task<HttpResponseInfo> request;
            TimeSpan interval;
            LinkedListNode<bool> turn;
            lock (gate) turn = waiting.AddLast(parallel);
            try
            {
                while (true)
                {
                    Task slot = null;
                    TimeSpan wait = TimeSpan.Zero;
                    bool backoff = false;
                    lock (gate)
                    {
                        token.ThrowIfCancellationRequested();
                        interval = TimeSpan.FromSeconds(Math.Min(PluginConfiguration.MaxApiRequestIntervalSeconds,
                            Math.Max(PluginConfiguration.MinApiRequestIntervalSeconds,
                                Math.Max(intervalSeconds, catalogueRequest ? catalogueIntervalSeconds : 0))));
                        var now = clock();
                        if (waiting.First != turn || (active != 0 && (!parallel || exclusive || serialCatalogue || active >= 2)))
                            slot = slotChanged.Task;
                        else
                        {
                            var earliest = lastRequestUtc == default(DateTime) ? now : lastRequestUtc + interval;
                            var pacingUntil = earliest > nextRequestUtc ? earliest : nextRequestUtc;
                            backoff = pacingUntil <= now && retryNotBeforeUtc > now;
                            wait = (backoff ? retryNotBeforeUtc : pacingUntil) - now;
                            if (wait <= TimeSpan.Zero)
                            {
                                if (timings != null)
                                    lock (timings.Gate) timings.RecordInterval((int)interval.TotalSeconds);
                                lastRequestUtc = now;
                                nextRequestUtc = now + interval;
                                active++;
                                exclusive = !parallel;
                                waiting.Remove(turn);
                                SignalWaiters();
                                try { request = send(); }
                                catch (Exception error) { request = Task.FromException<HttpResponseInfo>(error); }
                                break;
                            }
                        }
                    }
                    var elapsed = timings == null ? null : Stopwatch.StartNew();
                    if (backoff && timings != null) Interlocked.Increment(ref timings.BackoffWaiters);
                    try
                    {
                        if (slot != null)
                        {
                            var cancelled = NewSlotSignal();
                            using (token.Register(() => cancelled.TrySetCanceled()))
                                await (await Task.WhenAny(slot, cancelled.Task).ConfigureAwait(false)).ConfigureAwait(false);
                        }
                        else await delay(wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (timings != null)
                        {
                            if (backoff) Interlocked.Decrement(ref timings.BackoffWaiters);
                            lock (timings.Gate)
                            {
                                if (slot != null) timings.PacerQueue += elapsed.Elapsed;
                                else if (backoff) timings.Backoff += elapsed.Elapsed;
                                else timings.Pacing += elapsed.Elapsed;
                            }
                        }
                    }
                }
            }
            finally
            {
                lock (gate)
                    if (turn.List != null)
                    {
                        waiting.Remove(turn);
                        SignalWaiters();
                    }
            }

            HttpResponseInfo response = null;
            try { return response = await request.ConfigureAwait(false); }
            finally
            {
                lock (gate)
                {
                    ObserveResponse(response, catalogueRequest, intervalSeconds, (int)interval.TotalSeconds);
                    active--;
                    SignalWaiters();
                }
            }
        }

        private void SignalWaiters()
        {
            var signal = slotChanged;
            slotChanged = NewSlotSignal();
            signal.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewSlotSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private void ObserveResponse(HttpResponseInfo response, bool catalogueRequest, int configured, int interval)
        {
    var code = response == null ? 0 : (int)response.StatusCode;
    if (code == 429 || code == 503)
    {
        var retryAfter = response.Headers?.FirstOrDefault(pair =>
            pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value;
        var now = clock();
        var retryAt = RetryAt(retryAfter, now);
        if (catalogueRequest && code == 429)
        {
            catalogueIntervalSeconds = Math.Min(PluginConfiguration.MaxApiRequestIntervalSeconds,
                Math.Max(catalogueIntervalSeconds, interval * 2));
            consecutiveCatalogueRateLimits = Math.Min(3, consecutiveCatalogueRateLimits + 1);
            var cooldown = now.AddSeconds(30 * (1 << (consecutiveCatalogueRateLimits - 1)));
            if (!retryAt.HasValue || retryAt.Value < cooldown) retryAt = cooldown;
        }
        if (code == 429 || retryAt.HasValue) serialCatalogue = true;
        if (retryAt.HasValue && retryAt.Value > retryNotBeforeUtc) retryNotBeforeUtc = retryAt.Value;
    }
    if (!catalogueRequest) return;
    if (code != 200) { consecutiveCatalogueSuccesses = 0; return; }
    // A response already in flight when throttled must not count as recovery.
    if (clock() < retryNotBeforeUtc || (serialCatalogue && active > 1)) return;
    consecutiveCatalogueRateLimits = 0;
    if ((serialCatalogue || catalogueIntervalSeconds > configured) &&
        ++consecutiveCatalogueSuccesses >= SuccessesBeforeRecovery)
    {
        serialCatalogue = false;
        consecutiveCatalogueSuccesses = 0;
        if (catalogueIntervalSeconds > configured)
        {
            catalogueIntervalSeconds--;
            nextRequestUtc = lastRequestUtc.AddSeconds(Math.Max(PluginConfiguration.MinApiRequestIntervalSeconds,
                Math.Max(configured, catalogueIntervalSeconds)));
        }
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
