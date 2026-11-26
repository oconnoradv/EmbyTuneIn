using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInPlayback
    {
        private const string PreRollPath = "/multi_bump_sonic_pre_pre.mp3";
        private readonly TuneInDirectory directory;
        private readonly Func<HttpRequestOptions, Task<HttpResponseInfo>> send;
        private readonly TuneInRequestPacer pacer;
        private readonly Action<string> warning;
        private readonly TimeSpan queueTimeout;
        private readonly TimeSpan openingTimeout;
        private readonly TimeSpan playlistTimeout;
        private readonly SemaphoreSlim externalOpenings;
        private static readonly SemaphoreSlim SharedExternalOpenings = new SemaphoreSlim(4);
        private static readonly TuneInPlaybackAttempts<PlaybackPlan> Attempts =
            new TuneInPlaybackAttempts<PlaybackPlan>();

        internal TuneInPlayback(TuneInDirectory directory, IHttpClient client, Action<string> warning,
            TuneInRequestPacer pacer = null, TimeSpan? queueTimeout = null, TimeSpan? openingTimeout = null,
            TimeSpan? playlistTimeout = null, SemaphoreSlim externalOpenings = null)
        {
            this.directory = directory;
            send = client == null ? (Func<HttpRequestOptions, Task<HttpResponseInfo>>)TuneInPlaybackHttp.SendAsync
                : options => client.SendAsync(options, "GET");
            this.warning = warning;
            this.pacer = pacer ?? TuneInRequests.Pacer;
            this.queueTimeout = queueTimeout ?? TimeSpan.FromSeconds(30);
            this.openingTimeout = openingTimeout ?? TimeSpan.FromSeconds(30);
            this.playlistTimeout = playlistTimeout ?? TimeSpan.FromSeconds(30);
            this.externalOpenings = externalOpenings ?? SharedExternalOpenings;
        }

        internal async Task<IEnumerable<MediaSourceInfo>> GetSourcesAsync(string id, IServerApplicationHost host,
            CancellationToken token)
        {
            var snapshot = directory.Snapshot();
            var plan = await ResolveScopedAsync(snapshot, id, token).ConfigureAwait(false);
            directory.CheckCurrent(snapshot, token);
            var relay = plan.HasPreRoll ? plan : plan.WithoutHls();
            if (relay == null)
            {
                if (snapshot.Value.AllowDirectHlsPlayback) return plan.Leaves.Select(DirectSource).ToArray();
                warning?.Invoke("TuneIn station offers only HLS streams; direct HLS playback is disabled in settings.");
                throw new InvalidDataException("This station only offers HLS streams. Enable Allow direct HLS playback "
                    + "in the TuneIn advanced settings to play it.");
            }
            if (host == null) throw new InvalidOperationException("The Emby playback host is unavailable.");
            var local = new Uri(host.GetLocalApiUrl(IPAddress.Loopback));
            if (!IPAddress.TryParse(local.Host, out var address) || !IPAddress.IsLoopback(address))
                throw new InvalidOperationException("TuneIn playback requires a server-loopback URL.");
            var attempt = Attempts.Add(directory, snapshot.Key, id, relay);
            using (var hash = SHA256.Create())
                return new[] { new MediaSourceInfo
                {
                    Id = (relay.HasPreRoll ? "tunein-preroll-v1-" : "tunein-relay-v1-")
                        + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-", ""),
                    Name = "TuneIn",
                    Path = local.AbsoluteUri.TrimEnd('/') + "/TuneIn/Playback/" + Uri.EscapeDataString(id) + "/" + attempt,
                    Protocol = MediaProtocol.Http,
                    IsRemote = true,
                    SupportsDirectPlay = false,
                    SupportsDirectStream = false,
                    SupportsTranscoding = true
                } };
        }

        // Resolution without the relay handoff, so playlist expansion can be verified directly.
        internal async Task<string[]> ResolveStreamsAsync(string id, CancellationToken token)
        {
            var snapshot = directory.Snapshot();
            return (await ResolveScopedAsync(snapshot, id, token).ConfigureAwait(false)).Leaves.ToArray();
        }

        private async Task<PlaybackPlan> ResolveScopedAsync(ConfigurationSnapshot snapshot, string id, CancellationToken token)
        {
            using (var scope = new PlaybackScope(directory, snapshot, token))
                return await ResolveAsync(snapshot, id, scope.Token).ConfigureAwait(false);
        }

        internal async Task<TuneInAudioSource> OpenAsync(string id,
            Func<TuneInAudioSource, CancellationToken, Stream> decode, CancellationToken token, string attempt = null)
        {
            var snapshot = directory.Snapshot();
            var entry = Validate(snapshot, id);
            var scope = new PlaybackScope(directory, snapshot, token, entry);
            TuneInAudioSource source = null;
            try
            {
                scope.Check();
                var plan = attempt == null ? null : Attempts.Take(directory, snapshot.Key, id, attempt);
                if (plan == null)
                {
                    var resolved = await ResolveAsync(snapshot, id, scope.Token).ConfigureAwait(false);
                    plan = (resolved.HasPreRoll ? resolved : resolved.WithoutHls())
                        ?? throw new InvalidDataException("TuneIn offers only HLS streams for this station, which the relay does not open.");
                }
                source = await OpenPlanAsync(plan, snapshot, scope, decode, scope.Token).ConfigureAwait(false);
                scope.Check();
                return new TuneInAudioSource(new ScopedStream(source, scope, warning), source.ContentType);
            }
            catch
            {
                try { source?.Dispose(); }
                finally { scope.Dispose(); }
                throw;
            }
        }

        private NavigationEntry Validate(ConfigurationSnapshot snapshot, string id)
        {
            var entry = directory.Resolve(snapshot, id);
            if (!TuneInBrowsing.ValidStation(entry.StationId))
                throw new InvalidOperationException("TuneIn item is not playable.");
            return entry;
        }

        private async Task<PlaybackPlan> ResolveAsync(ConfigurationSnapshot snapshot, string id, CancellationToken token)
        {
            var entry = Validate(snapshot, id);
            try
            {
                var stationId = entry.PodcastShow
                    ? await ResolvePodcastEpisodeAsync(entry.StationId, snapshot, token).ConfigureAwait(false)
                    : entry.StationId;
                var request = new DirectoryRequest { Method = "Tune.ashx" };
                request.Parameters["id"] = stationId;
                request.Parameters["partnerid"] = "uD1X52pA";
                request.Parameters["formats"] = "mp3,aac";
                if (entry.Category == "presets") request.Parameters["username"] = snapshot.Value.Username;
                var playlist = await ReadPlaylistAsync(request.Url, snapshot, token).ConfigureAwait(false);
                return await ExpandAsync(ParseUrls(playlist.Text, playlist.Url, provider: true), snapshot, 0,
                    new HashSet<string>(StringComparer.Ordinal) { playlist.Url.AbsoluteUri }, new int[1], token)
                    .ConfigureAwait(false);
            }
            catch (PlaylistLimitFailure error)
            {
                warning?.Invoke("TuneIn playback resolution failed: " + error.Message);
                throw new InvalidDataException(error.Message);
            }
            catch (Exception error) when (IsPlaybackFailure(error))
            {
                warning?.Invoke("TuneIn playback resolution failed: " + DescribeFailure(error));
                throw;
            }
        }

        private async Task<string> ResolvePodcastEpisodeAsync(string showId, ConfigurationSnapshot snapshot, CancellationToken token)
        {
            var request = new DirectoryRequest { Method = "Tune.ashx" };
            request.Parameters["c"] = "pbrowse";
            request.Parameters["id"] = showId;
            request.Parameters["partnerid"] = "uD1X52pA";
            request.Parameters["formats"] = "mp3,aac";
            var listing = await ReadPlaylistAsync(request.Url, snapshot, token).ConfigureAwait(false);
            directory.CheckCurrent(snapshot, token);
            var episode = SelectPodcastEpisode(listing.Text, showId, snapshot.Value.PodcastEpisodeOrder, out var inferred);
            if (inferred)
                warning?.Invoke("TuneIn podcast episode dates are incomplete; playback uses the observed provider list order.");
            return episode;
        }

        internal static string SelectPodcastEpisode(string listing, string showId, string order, out bool inferred)
        {
            inferred = false;
            var document = TuneInDirectory.ParseCatalogue(Encoding.UTF8.GetBytes(listing));
            var sections = document.Root.Element("body").Elements("outline")
                .Where(section => TuneInDirectory.Attribute(section, "key") == "topics").ToArray();
            var rows = sections.SelectMany(section => section.Descendants("outline"))
                .Where(row => TuneInDirectory.Attribute(row, "type") == "audio")
                .Select(row =>
                {
                    var episode = TuneInDirectory.Attribute(row, "guide_id");
                    var url = TuneInDirectory.Attribute(row, "URL");
                    if (episode == null || !System.Text.RegularExpressions.Regex.IsMatch(episode, @"\At[1-9][0-9]{0,9}\z")
                        || !TuneInDirectory.IsPlaybackLink(url)) return null;
                    var link = DirectoryRequest.ParseLink(url, _ => true, allowSkippingTune: true);
                    if (link == null || link.Parameters.GetValueOrDefault("id") != episode
                        || link.Parameters.GetValueOrDefault("sid") != showId) return null;
                    var text = TuneInDirectory.Attribute(row, "published") ?? TuneInDirectory.Attribute(row, "date");
                    DateTimeOffset date = default;
                    var hasDate = text != null && DateTimeOffset.TryParseExact(text,
                        new[] { "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "r" },
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
                    return new { Id = episode, Date = hasDate ? (DateTimeOffset?)date : null };
                }).Where(row => row != null).ToArray();
            if (rows.Length == 0) throw new InvalidDataException("TuneIn podcast show returned no verified playable episodes.");
            if (order == "Provider")
            {
                var providerOrder = sections.Length == 1 ? TuneInDirectory.Attribute(sections[0], "order") : null;
                if (new[] { "asc", "desc", "oldest", "latest", "provider" }.Contains(providerOrder, StringComparer.OrdinalIgnoreCase))
                    return rows[0].Id;
                order = "Latest";
            }
            if (rows.All(row => row.Date.HasValue))
                return (order == "Oldest" ? rows.OrderBy(row => row.Date.Value)
                    : rows.OrderByDescending(row => row.Date.Value)).First().Id;
            inferred = true;
            return order == "Oldest" ? rows[rows.Length - 1].Id : rows[0].Id;
        }

        private async Task<PlaybackPlan> ExpandAsync(List<string> urls, ConfigurationSnapshot snapshot, int depth,
            HashSet<string> ancestors, int[] requests, CancellationToken token)
        {
            var plan = new PlaybackPlan();
            Exception failure = null;
            foreach (var url in urls)
            {
                directory.CheckCurrent(snapshot, token);
                if (IsPreRoll(url))
                {
                    if (plan.Alternatives.Count != 0)
                        throw new InvalidDataException("TuneIn returned a pre-roll after a live alternative.");
                    plan.PreRolls.Add(url);
                    continue;
                }
                var extension = Path.GetExtension(new Uri(url).AbsolutePath);
                if (extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".pls", StringComparison.OrdinalIgnoreCase))
                {
                    if (depth >= 4 || !ancestors.Add(url))
                        throw new InvalidDataException("TuneIn playlist resolution exceeds its nesting or request limit.");
                    try
                    {
                            ConsumeNestedRequest(requests);
                            (string Text, Uri Url) playlist;
                            try
                            {
                                playlist = await ReadPlaylistAsync(url, snapshot, token, () => ConsumeNestedRequest(requests))
                                    .ConfigureAwait(false);
                            }
                            catch (Exception error) when (IsPlaybackFailure(error) && !(error is PlaylistLimitFailure))
                            {
                                directory.CheckCurrent(snapshot, token);
                                failure = error;
                                warning?.Invoke("TuneIn nested playlist could not be fetched: " + DescribeFailure(error));
                                continue;
                            }
                            var redirected = playlist.Url.AbsoluteUri != url;
                            if (redirected && !ancestors.Add(playlist.Url.AbsoluteUri))
                                throw new InvalidDataException("TuneIn playlist resolution exceeds its nesting or request limit.");
                            try
                            {
                                // Leave native HLS handling intact, but never delegate a known TuneIn pre-roll.
                                if (playlist.Text.Contains("#EXT-X-") && !MentionsPreRoll(playlist.Text))
                                    plan.Alternatives.Add(new PlaybackPlan { Url = playlist.Url.AbsoluteUri, Hls = true });
                                else
                                {
                                    List<string> nested;
                                    try { nested = ParseUrls(playlist.Text, playlist.Url); }
                                    catch (InvalidDataException error) when (!MentionsPreRoll(playlist.Text))
                                    {
                                        directory.CheckCurrent(snapshot, token);
                                        failure = error;
                                        warning?.Invoke("TuneIn nested playlist could not be parsed: " + DescribeFailure(error));
                                        continue;
                                    }
                                    try
                                    {
                                        plan.Alternatives.Add(await ExpandAsync(nested, snapshot, depth + 1,
                                            ancestors, requests, token).ConfigureAwait(false));
                                    }
                                    catch (NoUsablePlaylist error) { failure = error.Failure; }
                                }
                            }
                            finally { if (redirected) ancestors.Remove(playlist.Url.AbsoluteUri); }
                    }
                    finally { ancestors.Remove(url); }
                }
                else plan.Alternatives.Add(new PlaybackPlan { Url = url });
            }
            if (plan.Alternatives.Count == 0)
            {
                if (plan.PreRolls.Count != 0)
                    throw new InvalidDataException("TuneIn returned no usable live stream after its required pre-roll.");
                throw new NoUsablePlaylist(failure);
            }
            return plan;
        }

        private static void ConsumeNestedRequest(int[] requests)
        {
            if (++requests[0] > 16)
                throw new PlaylistLimitFailure("TuneIn playlist resolution exceeds its nested request limit.");
        }

        private async Task<(string Text, Uri Url)> ReadPlaylistAsync(string url, ConfigurationSnapshot snapshot,
            CancellationToken token, Action countRedirect = null)
        {
            using (var source = await FetchAsync(url, snapshot, token, countRedirect).ConfigureAwait(false))
            using (var memory = new MemoryStream())
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(playlistTimeout);
                using (timeout.Token.Register(() => DisposeQuietly(source)))
                {
                    var buffer = new byte[8192];
                    try
                    {
                        int count;
                        while ((count = await source.Body.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                        {
                            directory.CheckCurrent(snapshot, timeout.Token);
                            if (memory.Length + count > 256 * 1024)
                                throw new PlaylistLimitFailure("TuneIn playback playlist exceeds its size limit.");
                            memory.Write(buffer, 0, count);
                        }
                        timeout.Token.ThrowIfCancellationRequested();
                        return (Encoding.UTF8.GetString(memory.ToArray()), source.EffectiveUrl);
                    }
                    catch (Exception error) when (!(error is PlaylistLimitFailure)
                        && (error is IOException || error is InvalidDataException || error is ObjectDisposedException
                            || error is OperationCanceledException || error is TimeoutException || error is HttpRequestException))
                    {
                        directory.CheckCurrent(snapshot, token);
                        if (timeout.IsCancellationRequested || error is TimeoutException || error is OperationCanceledException)
                            throw new PlaybackTimeout("TuneIn playback playlist body read timed out.");
                        throw new PlaybackFailure("TuneIn playback playlist body could not be read.");
                    }
                }
            }
        }

        private async Task<TuneInAudioSource> FetchAsync(string url, ConfigurationSnapshot snapshot,
            CancellationToken token, Action countRedirect = null)
        {
            var uri = ValidateUrl(url);
            var originalApi = IsApiHost(uri);
            var originalPreRoll = IsPreRoll(url);
            for (var redirects = 0; ; redirects++)
            {
                var source = await FetchHopAsync(uri, snapshot, token).ConfigureAwait(false);
                if (source.StatusCode == HttpStatusCode.OK) return source;
                using (source)
                {
                    var status = (int)source.StatusCode;
                    if (status != 301 && status != 302 && status != 303 && status != 307 && status != 308)
                        throw new PlaybackFailure("TuneIn playback opening returned HTTP " + status + ".");
                    if (redirects == 4 || string.IsNullOrEmpty(source.Location)
                        || source.Location.Length > 4096 || !Uri.TryCreate(uri, source.Location, out var next))
                        throw new PlaybackFailure("TuneIn playback redirect is invalid or exceeds its limit.");
                    next = ValidateUrl(next.AbsoluteUri);
                    if ((uri.Scheme == "https" && next.Scheme != "https")
                        || (originalApi && (!IsApiHost(next) || !next.IsDefaultPort))
                        || (!originalApi && IsApiHost(next))
                        || (!originalPreRoll && IsPreRoll(next.AbsoluteUri)))
                        throw new PlaybackFailure("TuneIn playback redirect is not permitted.");
                    countRedirect?.Invoke();
                    uri = next;
                }
            }
        }

        private async Task<TuneInAudioSource> FetchHopAsync(Uri uri, ConfigurationSnapshot snapshot, CancellationToken token)
        {
            directory.CheckCurrent(snapshot, token);
            var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using (var queue = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                queue.CancelAfter(queueTimeout);
                var started = false;
                var externalSlot = false;
                HttpResponseInfo response = null;
                try
                {
                    Task<HttpResponseInfo> Start()
                    {
                        queue.Token.ThrowIfCancellationRequested();
                        started = true;
                        queue.CancelAfter(Timeout.InfiniteTimeSpan);
                        operation.Token.ThrowIfCancellationRequested();
                        operation.CancelAfter(openingTimeout);
                        return send(new HttpRequestOptions
                        {
                            Url = uri.AbsoluteUri, CancellationToken = operation.Token, ThrowOnErrorResponse = false,
                            BufferContent = false, UserAgent = TuneInPlaybackHttp.UserAgent
                        });
                    }
                    if (IsApiHost(uri))
                        response = await pacer.SendAsync(Start, snapshot.Value.ApiRequestIntervalSeconds, queue.Token)
                            .ConfigureAwait(false);
                    else
                    {
                        await externalOpenings.WaitAsync(queue.Token).ConfigureAwait(false);
                        externalSlot = true;
                        response = await Start().ConfigureAwait(false);
                    }
                    operation.Token.ThrowIfCancellationRequested();
                    directory.CheckCurrent(snapshot, token);
                    operation.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (response.Content == null && response.StatusCode == HttpStatusCode.OK)
                        throw new PlaybackFailure("TuneIn playback response has no content.");
                    return new TuneInAudioSource(response.Content ?? Stream.Null,
                        (response.ContentType ?? "application/octet-stream").Split(';')[0].Trim().ToLowerInvariant(),
                        new HttpOwner(response, operation), uri, response.StatusCode, response.Headers?
                            .FirstOrDefault(pair => pair.Key.Equals("Location", StringComparison.OrdinalIgnoreCase)).Value);
                }
                catch (Exception error)
                {
                    var timedOut = operation.IsCancellationRequested;
                    DisposeQuietly(response?.Content);
                    DisposeQuietly(response);
                    operation.Dispose();
                    directory.CheckCurrent(snapshot, token);
                    if (!started && queue.IsCancellationRequested) throw new PlaybackTimeout("TuneIn playback queue wait timed out.");
                    if (timedOut || error is TimeoutException || error is OperationCanceledException)
                        throw new PlaybackTimeout("TuneIn playback network opening timed out.");
                    if (error is PlaybackFailure) throw;
                    if (error is TuneInPlaybackHttp.PinnedConnectionsUnavailableException)
                        throw new PlaybackFailure(error.Message);
                    if (error is HttpRequestException || error is IOException || error is InvalidDataException || error is WebException)
                        throw new PlaybackFailure("TuneIn playback network opening failed.");
                    throw;
                }
                finally { if (externalSlot) externalOpenings.Release(); }
            }
        }

        private async Task<TuneInAudioSource> OpenPlanAsync(PlaybackPlan plan, ConfigurationSnapshot snapshot,
            PlaybackScope scope, Func<TuneInAudioSource, CancellationToken, Stream> decode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            scope.Check();
            if (plan.Url != null)
            {
                scope.ConsumeOpening();
                var source = await FetchAsync(plan.Url, snapshot, token).ConfigureAwait(false);
                if (IsRelayableAudio(source.ContentType))
                    return source;
                source.Dispose();
                throw new InvalidDataException("Unsupported TuneIn pre-roll/live audio format.");
            }
            if (plan.PreRolls.Count != 0)
            {
                if (decode == null) throw new InvalidOperationException("The Emby pre-roll audio decoder is unavailable.");
                var stages = new Queue<Func<CancellationToken, Task<TuneInAudioSource>>>();
                foreach (var url in plan.PreRolls)
                    stages.Enqueue(ct => { scope.Check(); return OpenPlanAsync(new PlaybackPlan { Url = url }, scope.Snapshot, scope, decode, ct); });
                stages.Enqueue(ct => { scope.Check(); return OpenAlternativesAsync(plan, scope.Snapshot, scope, decode, ct); });
                var first = await stages.Dequeue()(token).ConfigureAwait(false);
                return new TuneInAudioSource(new PcmSequenceStream(first, stages, decode, token), "audio/wav");
            }
            return await OpenAlternativesAsync(plan, snapshot, scope, decode, token).ConfigureAwait(false);
        }

        private async Task<TuneInAudioSource> OpenAlternativesAsync(PlaybackPlan plan, ConfigurationSnapshot snapshot,
            PlaybackScope scope, Func<TuneInAudioSource, CancellationToken, Stream> decode, CancellationToken token)
        {
            Exception failure = null;
            foreach (var alternative in plan.Alternatives)
            {
                scope.Check();
                token.ThrowIfCancellationRequested();
                try { return await OpenPlanAsync(alternative, snapshot, scope, decode, token).ConfigureAwait(false); }
                catch (Exception error) when (!alternative.HasPreRoll && (error is IOException || error is TimeoutException))
                {
                    failure = error;
                    warning?.Invoke("TuneIn live stream opening failed: " + DescribeFailure(error));
                }
            }
            if (failure != null) throw failure;
            throw new PlaybackFailure("No TuneIn live alternative could be opened.");
        }

        // Playlists and web pages are never relayed as audio; ffmpeg probes the actual codec.
        private static bool IsRelayableAudio(string contentType) =>
            contentType == "application/octet-stream" || contentType == "application/ogg"
            || (contentType != null && contentType.StartsWith("audio/", StringComparison.Ordinal)
                && contentType.IndexOf("mpegurl", StringComparison.Ordinal) < 0
                && contentType.IndexOf("scpls", StringComparison.Ordinal) < 0);

        internal static bool IsPreRoll(string value)
        {
            var uri = ValidateUrl(value);
            return uri.Scheme == "https" && uri.IsDefaultPort
                && uri.IdnHost.TrimEnd('.').Equals("tunein-ondemand.cdnstream1.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath == PreRollPath;
        }

        private static bool IsApiHost(Uri uri) =>
            uri.IdnHost.TrimEnd('.').Equals("opml.radiotime.com", StringComparison.OrdinalIgnoreCase);

        // If a malformed playlist mentions the required pre-roll, do not discard it and try live.
        private static bool MentionsPreRoll(string text) =>
            Uri.UnescapeDataString(text).IndexOf(PreRollPath, StringComparison.OrdinalIgnoreCase) >= 0;

        private static Uri ValidateUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http")
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || value.Length > 4096
                || uri.IsLoopback || uri.IdnHost.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.IdnHost.TrimEnd('.').EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && !IsPublicAddress(address))
                || (IsApiHost(uri) && (uri.Scheme != "https" || !uri.IsDefaultPort)))
                throw new InvalidDataException("Invalid TuneIn playback URL.");
            if (uri.AbsolutePath.IndexOf("/notcompatible.", StringComparison.OrdinalIgnoreCase) >= 0
                || uri.AbsolutePath.IndexOf("/nostream.", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidDataException("TuneIn reports that this station is unavailable or incompatible.");
            return uri;
        }

        internal static bool IsPublicAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address)) return false;
            var bytes = address.GetAddressBytes();
            if (bytes.Length == 16)
            {
                // Global unicast only, excluding Teredo, documentation and 6to4 wrappers around non-public IPv4.
                if ((bytes[0] & 0xe0) != 0x20 || address.IsIPv6Teredo) return false;
                if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return false;
                if (bytes[0] == 0x20 && bytes[1] == 0x02)
                    return IsPublicAddress(new IPAddress(new[] { bytes[2], bytes[3], bytes[4], bytes[5] }));
                return true;
            }
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && bytes[0] < 224
                && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && (bytes[1] & 0xc0) == 64)
                && !(bytes[0] == 192 && bytes[1] == 0 && (bytes[2] == 0 || bytes[2] == 2))
                && !(bytes[0] == 198 && (bytes[1] & 0xfe) == 18)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }

        private static List<string> ParseUrls(string text, Uri parent, bool provider = false)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().TrimStart('\uFEFF')).Where(line => line.Length != 0).ToArray();
            IEnumerable<string> values = lines.Where(line => !line.StartsWith("#", StringComparison.Ordinal));
            if (lines.Any(line => line.Equals("[playlist]", StringComparison.OrdinalIgnoreCase)))
            {
                var entries = new SortedDictionary<int, string>();
                foreach (var line in lines.Where(line => line.StartsWith("File", StringComparison.OrdinalIgnoreCase)))
                {
                    var equals = line.IndexOf('=');
                    if (equals < 5 || !int.TryParse(line.Substring(4, equals - 4), out var number) || number < 1
                        || entries.ContainsKey(number))
                        throw new InvalidDataException("Invalid TuneIn PLS entry.");
                    entries.Add(number, line.Substring(equals + 1).Trim());
                }
                values = entries.Values;
            }
            var urls = new List<string>();
            foreach (var value in values)
            {
                if (urls.Count == 8) throw new PlaylistLimitFailure("TuneIn playback playlist exceeds its entry limit.");
                if (!Uri.TryCreate(parent, value, out var uri))
                    throw new InvalidDataException("Invalid TuneIn playback playlist entry.");
                urls.Add(ValidateUrl(uri.AbsoluteUri).AbsoluteUri);
            }
            if (urls.Count == 0)
            {
                if (provider)
                    foreach (var line in lines.Where(line => line.StartsWith("#STATUS:", StringComparison.OrdinalIgnoreCase)))
                        if (int.TryParse(line.Substring(8).Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                            out var status) && status >= 400 && status <= 599)
                            throw new PlaybackFailure("TuneIn returned HTTP 200 with playlist status " + status + " and no URLs.");
                throw new InvalidDataException("TuneIn returned an empty playback playlist.");
            }
            return urls;
        }

        private static bool IsPlaybackFailure(Exception error) => error is IOException || error is InvalidDataException
            || error is PlaybackTimeout;

        internal static string DescribeFailure(Exception error)
        {
            if (error is NoUsablePlaylist noPlaylist)
                return noPlaylist.Failure == null ? noPlaylist.Message : DescribeFailure(noPlaylist.Failure);
            if (error is PlaybackFailure || error is PlaybackTimeout || error is PlaylistLimitFailure
                || error is InvalidDataException)
                return error.Message;
            return error.GetType().Name;
        }

        private static void DisposeQuietly(IDisposable value)
        {
            try { value?.Dispose(); }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException
                || error is AggregateException) { }
        }

        private sealed class PlaybackFailure : IOException
        {
            internal PlaybackFailure(string message) : base(message) { }
        }

        private sealed class PlaylistLimitFailure : IOException
        {
            internal PlaylistLimitFailure(string message) : base(message) { }
        }

        private sealed class PlaybackTimeout : TimeoutException
        {
            internal PlaybackTimeout(string message) : base(message) { }
        }

        private sealed class NoUsablePlaylist : IOException
        {
            internal readonly Exception Failure;
            internal NoUsablePlaylist(Exception failure)
                : base(failure == null ? "TuneIn returned no usable playback alternatives." : DescribeFailure(failure))
            { Failure = failure; }
        }

        private static MediaSourceInfo DirectSource(string url)
        {
            var path = new Uri(url).AbsolutePath;
            var container = path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls"
                : path.EndsWith("aac", StringComparison.OrdinalIgnoreCase) ? "aac" : "mp3";
            return new ChannelMediaInfo
            {
                Path = url, Container = container, AudioCodec = container == "hls" ? null : container,
                AudioBitrate = 128000, AudioChannels = 2, SupportsDirectPlay = true
            }.ToMediaSource();
        }

        private sealed class PlaybackPlan
        {
            internal string Url;
            internal bool Hls;
            internal readonly List<string> PreRolls = new List<string>();
            internal readonly List<PlaybackPlan> Alternatives = new List<PlaybackPlan>();
            internal bool HasPreRoll => PreRolls.Count != 0 || Alternatives.Any(plan => plan.HasPreRoll);
            internal IEnumerable<string> Leaves => Url == null ? Alternatives.SelectMany(plan => plan.Leaves) : new[] { Url };
            internal bool IsHls => Url != null
                && (Hls || new Uri(Url).AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));

            // The relay validates every connection itself; HLS would hand segment fetching to Emby unchecked.
            internal PlaybackPlan WithoutHls()
            {
                if (Url != null) return IsHls ? null : this;
                var copy = new PlaybackPlan();
                copy.PreRolls.AddRange(PreRolls);
                copy.Alternatives.AddRange(Alternatives.Select(plan => plan.WithoutHls()).Where(plan => plan != null));
                return copy.Alternatives.Count == 0 ? null : copy;
            }
        }

        private sealed class HttpOwner : IDisposable
        {
            private readonly HttpResponseInfo response;
            private readonly CancellationTokenSource operation;
            private readonly CancellationTokenRegistration cancellation;
            internal HttpOwner(HttpResponseInfo response, CancellationTokenSource operation)
            {
                this.response = response;
                this.operation = operation;
                cancellation = operation.Token.Register(() => DisposeQuietly(response.Content));
            }
            public void Dispose()
            {
                try { operation.Cancel(); }
                finally
                {
                    cancellation.Dispose();
                    try { DisposeQuietly(response.Content); }
                    finally { try { DisposeQuietly(response); } finally { operation.Dispose(); } }
                }
            }
        }

        internal static void CancelChangedScopes() => PlaybackScope.CancelChanged();

        private sealed class PlaybackScope : IDisposable
        {
            private static readonly object Gate = new object();
            private static readonly HashSet<PlaybackScope> Active = new HashSet<PlaybackScope>();
            private readonly TuneInDirectory directory;
            private readonly NavigationEntry entry;
            private volatile ConfigurationSnapshot snapshot;
            private readonly CancellationTokenSource lifetime;
            private int openings;
            internal CancellationToken Token { get; }
            internal ConfigurationSnapshot Snapshot => snapshot;
            internal PlaybackScope(TuneInDirectory directory, ConfigurationSnapshot snapshot, CancellationToken token,
                NavigationEntry entry = null)
            {
                this.directory = directory;
                this.snapshot = snapshot;
                this.entry = entry;
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                Token = lifetime.Token;
                lock (Gate)
                {
                    if (Active.Count >= 16)
                    {
                        lifetime.Dispose();
                        throw new InvalidOperationException("TuneIn has too many active playback requests.");
                    }
                    Active.Add(this);
                }
            }
            internal void Check()
            {
                Token.ThrowIfCancellationRequested();
                if (!Follow(directory.Snapshot())) throw new OperationCanceledException("TuneIn configuration changed.");
            }
            private bool Follow(ConfigurationSnapshot current)
            {
                lock (this)
                {
                    if (current.Key == snapshot.Key) return true;
                    if (!TuneInDirectory.PlaybackStillAllowed(snapshot, current, entry)) return false;
                    snapshot = current;
                    return true;
                }
            }
            internal void ConsumeOpening()
            {
                if (Interlocked.Increment(ref openings) > 16)
                    throw new InvalidOperationException("TuneIn exceeded its audio-opening attempt limit.");
            }
            internal static void CancelChanged()
            {
                lock (Gate)
                    foreach (var scope in Active)
                        if (!scope.Follow(scope.directory.Snapshot())) scope.lifetime.Cancel();
            }
            public void Dispose()
            {
                lock (Gate)
                    if (Active.Remove(this))
                    {
                        try { lifetime.Cancel(); }
                        finally { lifetime.Dispose(); }
                    }
            }
        }

        private sealed class ScopedStream : RadioReadStream
        {
            private readonly TuneInAudioSource source;
            private readonly PlaybackScope scope;
            private readonly Action<string> warning;
            private bool disposed;
            private bool failed;
            internal ScopedStream(TuneInAudioSource source, PlaybackScope scope, Action<string> warning)
            { this.source = source; this.scope = scope; this.warning = warning; }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ScopedStream));
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(token, scope.Token))
                    try
                    {
                        scope.Check();
                        var size = await source.Body.ReadAsync(buffer, offset, count, read.Token).ConfigureAwait(false);
                        scope.Check();
                        return size;
                    }
                    catch (Exception error)
                    {
                        if (!failed && !(error is OperationCanceledException))
                            warning("TuneIn pre-roll/live playback failed (" + error.GetType().Name + ").");
                        failed = true;
                        Dispose();
                        throw;
                    }
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing && !disposed)
                {
                    disposed = true;
                    try { scope.Dispose(); }
                    finally { source.Dispose(); }
                }
                base.Dispose(disposing);
            }
        }
    }

    internal sealed class TuneInAudioSource : IDisposable
    {
        internal Stream Body { get; }
        internal string ContentType { get; }
        internal Uri EffectiveUrl { get; }
        internal HttpStatusCode StatusCode { get; }
        internal string Location { get; }
        private readonly IDisposable owner;
        private int disposed;
        internal TuneInAudioSource(Stream body, string contentType, IDisposable owner = null,
            Uri effectiveUrl = null, HttpStatusCode statusCode = HttpStatusCode.OK, string location = null)
        {
            Body = body; ContentType = contentType; this.owner = owner;
            EffectiveUrl = effectiveUrl; StatusCode = statusCode; Location = location;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                if (owner == null) Body.Dispose();
                else owner.Dispose();
            }
        }
    }
}
