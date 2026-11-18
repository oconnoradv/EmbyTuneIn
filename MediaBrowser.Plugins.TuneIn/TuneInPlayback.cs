using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInPlayback
    {
        private readonly TuneInDirectory directory;
        private readonly IHttpClient client;
        private readonly Action<string> warning;

        internal TuneInPlayback(TuneInDirectory directory, IHttpClient client, Action<string> warning)
        {
            this.directory = directory;
            this.client = client;
            this.warning = warning;
        }

        internal async Task<IEnumerable<MediaSourceInfo>> GetSourcesAsync(string id, IServerApplicationHost host,
            CancellationToken token)
        {
            var snapshot = directory.Snapshot();
            var plan = await ResolveAsync(snapshot, id, token).ConfigureAwait(false);
            directory.CheckCurrent(snapshot, token);
            if (!plan.HasPreRoll)
                return plan.Leaves.Select(DirectSource).ToArray();
            if (host == null) throw new InvalidOperationException("The Emby playback host is unavailable.");
            var local = new Uri(host.GetLocalApiUrl(IPAddress.Loopback));
            if (!IPAddress.TryParse(local.Host, out var address) || !IPAddress.IsLoopback(address))
                throw new InvalidOperationException("TuneIn playback requires a server-loopback URL.");
            using (var hash = SHA256.Create())
                return new[] { new MediaSourceInfo
                {
                    Id = "tunein-preroll-v1-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-", ""),
                    Name = "TuneIn",
                    Path = local.AbsoluteUri.TrimEnd('/') + "/TuneIn/Playback/" + Uri.EscapeDataString(id),
                    Protocol = MediaProtocol.Http,
                    IsRemote = true,
                    SupportsDirectPlay = false,
                    SupportsDirectStream = false,
                    SupportsTranscoding = true
                } };
        }

        internal async Task<TuneInAudioSource> OpenAsync(string id,
            Func<TuneInAudioSource, CancellationToken, Stream> decode, CancellationToken token)
        {
            var snapshot = directory.Snapshot();
            Validate(snapshot, id);
            var scope = new PlaybackScope(directory, snapshot, token);
            TuneInAudioSource source = null;
            try
            {
                var plan = await ResolveAsync(snapshot, id, scope.Token).ConfigureAwait(false);
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
            var request = new DirectoryRequest { Method = "Tune.ashx" };
            request.Parameters["id"] = entry.StationId;
            request.Parameters["partnerid"] = "uD1X52pA";
            request.Parameters["formats"] = "mp3,aac";
            if (entry.Category == "presets") request.Parameters["username"] = snapshot.Value.Username;
            var text = await ReadPlaylistAsync(request.Url, snapshot, token).ConfigureAwait(false);
            return await ExpandAsync(ParseUrls(text, new Uri(request.Url)), snapshot, 0,
                new HashSet<string>(StringComparer.Ordinal), new int[1], token).ConfigureAwait(false);
        }

        private async Task<PlaybackPlan> ExpandAsync(List<string> urls, ConfigurationSnapshot snapshot, int depth,
            HashSet<string> ancestors, int[] requests, CancellationToken token)
        {
            var plan = new PlaybackPlan();
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
                    if (depth >= 4 || ++requests[0] > 16 || !ancestors.Add(url))
                        throw new InvalidDataException("TuneIn playlist resolution exceeds its nesting or request limit.");
                    try
                    {
                        var text = await ReadPlaylistAsync(url, snapshot, token).ConfigureAwait(false);
                        // Leave existing native HLS handling intact for ordinary stations.
                        plan.Alternatives.Add(text.Contains("#EXT-X-") ? new PlaybackPlan { Url = url }
                            : await ExpandAsync(ParseUrls(text, new Uri(url)), snapshot, depth + 1,
                                ancestors, requests, token).ConfigureAwait(false));
                    }
                    finally { ancestors.Remove(url); }
                }
                else plan.Alternatives.Add(new PlaybackPlan { Url = url });
            }
            if (plan.Alternatives.Count == 0)
                throw new InvalidDataException("TuneIn returned no live stream after its pre-roll.");
            return plan;
        }

        private async Task<string> ReadPlaylistAsync(string url, ConfigurationSnapshot snapshot, CancellationToken token)
        {
            using (var source = await FetchAsync(url, snapshot, token).ConfigureAwait(false))
            using (var memory = new MemoryStream())
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using (timeout.Token.Register(source.Dispose))
                {
                    var buffer = new byte[8192];
                    try
                    {
                        int count;
                        while ((count = await source.Body.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                        {
                            directory.CheckCurrent(snapshot, timeout.Token);
                            if (memory.Length + count > 256 * 1024)
                                throw new InvalidDataException("TuneIn playback playlist exceeds its size limit.");
                            memory.Write(buffer, 0, count);
                        }
                        timeout.Token.ThrowIfCancellationRequested();
                        return Encoding.UTF8.GetString(memory.ToArray());
                    }
                    catch
                    {
                        token.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested) throw new TimeoutException("TuneIn playback playlist timed out.");
                        throw;
                    }
                }
            }
        }

        private async Task<TuneInAudioSource> FetchAsync(string url, ConfigurationSnapshot snapshot, CancellationToken token)
        {
            ValidateUrl(url);
            directory.CheckCurrent(snapshot, token);
            var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            operation.CancelAfter(TimeSpan.FromSeconds(30 + Math.Min(30, Math.Max(1, snapshot.Value.ApiRequestIntervalSeconds))));
            HttpResponseInfo response = null;
            try
            {
                response = await TuneInRequests.SendAsync(client, new HttpRequestOptions
                {
                    Url = url, CancellationToken = operation.Token, ThrowOnErrorResponse = false
                }, "GET", snapshot.Value.ApiRequestIntervalSeconds, bufferContent: false).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                directory.CheckCurrent(snapshot, token);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("TuneIn playback returned HTTP " + (int)response.StatusCode + ".");
                operation.CancelAfter(Timeout.InfiniteTimeSpan);
                return new TuneInAudioSource(response.Content,
                    (response.ContentType ?? "application/octet-stream").Split(';')[0].Trim().ToLowerInvariant(),
                    new HttpOwner(response, operation));
            }
            catch
            {
                var timedOut = operation.IsCancellationRequested;
                try { response?.Content?.Dispose(); }
                finally { response?.Dispose(); operation.Dispose(); }
                token.ThrowIfCancellationRequested();
                if (timedOut) throw new TimeoutException("TuneIn playback opening timed out.");
                throw;
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
                if (source.ContentType == "audio/mpeg" || source.ContentType == "audio/mp3"
                    || source.ContentType == "audio/aac" || source.ContentType == "audio/aacp"
                    || source.ContentType == "application/octet-stream")
                    return source;
                source.Dispose();
                throw new InvalidDataException("Unsupported TuneIn pre-roll/live audio format.");
            }
            if (plan.PreRolls.Count != 0)
            {
                if (decode == null) throw new InvalidOperationException("The Emby pre-roll audio decoder is unavailable.");
                var stages = new Queue<Func<CancellationToken, Task<TuneInAudioSource>>>();
                foreach (var url in plan.PreRolls)
                    stages.Enqueue(ct => OpenPlanAsync(new PlaybackPlan { Url = url }, snapshot, scope, decode, ct));
                stages.Enqueue(ct => OpenAlternativesAsync(plan, snapshot, scope, decode, ct));
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
                    warning("TuneIn live stream opening failed; trying another advertised alternative.");
                }
            }
            throw new IOException("No TuneIn live alternative could be opened.", failure);
        }

        internal static bool IsPreRoll(string value)
        {
            var uri = ValidateUrl(value);
            return uri.Scheme == "https" && uri.IsDefaultPort
                && uri.Host.Equals("tunein-ondemand.cdnstream1.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath == "/multi_bump_sonic_pre_pre.mp3";
        }

        private static Uri ValidateUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http")
                || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || value.Length > 4096)
                throw new InvalidDataException("Invalid TuneIn playback URL.");
            if (uri.AbsolutePath.IndexOf("/notcompatible.", StringComparison.OrdinalIgnoreCase) >= 0
                || uri.AbsolutePath.IndexOf("/nostream.", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidDataException("TuneIn reports that this station is unavailable or incompatible.");
            return uri;
        }

        private static List<string> ParseUrls(string text, Uri parent)
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
                if (urls.Count == 8) throw new InvalidDataException("TuneIn playback playlist exceeds its entry limit.");
                if (!Uri.TryCreate(parent, value, out var uri))
                    throw new InvalidDataException("Invalid TuneIn playback playlist entry.");
                urls.Add(ValidateUrl(uri.AbsoluteUri).AbsoluteUri);
            }
            if (urls.Count == 0) throw new InvalidDataException("TuneIn returned an empty playback playlist.");
            return urls;
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
            internal readonly List<string> PreRolls = new List<string>();
            internal readonly List<PlaybackPlan> Alternatives = new List<PlaybackPlan>();
            internal bool HasPreRoll => PreRolls.Count != 0 || Alternatives.Any(plan => plan.HasPreRoll);
            internal IEnumerable<string> Leaves => Url == null ? Alternatives.SelectMany(plan => plan.Leaves) : new[] { Url };
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
                cancellation = operation.Token.Register(response.Content.Dispose);
            }
            public void Dispose()
            {
                try { operation.Cancel(); }
                finally
                {
                    cancellation.Dispose();
                    try { response.Content.Dispose(); }
                    finally { try { response.Dispose(); } finally { operation.Dispose(); } }
                }
            }
        }

        internal static void CancelChangedScopes() => PlaybackScope.CancelChanged();

        private sealed class PlaybackScope : IDisposable
        {
            private static readonly object Gate = new object();
            private static readonly HashSet<PlaybackScope> Active = new HashSet<PlaybackScope>();
            private readonly TuneInDirectory directory;
            private readonly ConfigurationSnapshot snapshot;
            private readonly CancellationTokenSource lifetime;
            private int openings;
            internal CancellationToken Token { get; }
            internal PlaybackScope(TuneInDirectory directory, ConfigurationSnapshot snapshot, CancellationToken token)
            {
                this.directory = directory;
                this.snapshot = snapshot;
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                Token = lifetime.Token;
                lock (Gate)
                {
                    if (Active.Count >= 16)
                    {
                        lifetime.Dispose();
                        throw new InvalidOperationException("TuneIn has too many active pre-roll playback requests.");
                    }
                    Active.Add(this);
                }
            }
            internal void Check() => directory.CheckCurrent(snapshot, Token);
            internal void ConsumeOpening()
            {
                if (Interlocked.Increment(ref openings) > 16)
                    throw new InvalidOperationException("TuneIn exceeded its audio-opening attempt limit.");
            }
            internal static void CancelChanged()
            {
                lock (Gate)
                    foreach (var scope in Active)
                        if (scope.directory.Snapshot().Key != scope.snapshot.Key) scope.lifetime.Cancel();
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
        private readonly IDisposable owner;
        private int disposed;
        internal TuneInAudioSource(Stream body, string contentType, IDisposable owner = null)
        { Body = body; ContentType = contentType; this.owner = owner; }
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
