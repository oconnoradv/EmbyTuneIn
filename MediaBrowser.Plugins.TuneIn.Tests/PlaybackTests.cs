using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Configuration;
using Moq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class PlaybackTests
    {
        private const string Ad = "https://tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3";
        private const string Live = "https://radio.example/Live.AAC?Signature=AbC";

        [Fact]
        public async Task SynchronousStalledReadTimesOutAndDoesNotExposeAnAbandonedReadBuffer()
        {
            using var stream = new StalledSyncStream();
            var buffer = new byte[] { 1, 2, 3 };
            var read = Task.Run(() => Assert.Throws<IOException>(() => stream.Read(buffer, 0, buffer.Length)));
            Assert.Contains("timed out", (await read.WaitAsync(TimeSpan.FromSeconds(5))).Message);
            Assert.True(stream.Disposed);
            stream.Finish();
            Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
        }

        [Fact]
        public async Task LegacyShoutcastStreamOpensWithUserAgentAfterHttpClientRejectsIcyStatus()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var requests = new List<string>();
            var server = Task.Run(async () =>
            {
                for (var connection = 0; connection < 2; connection++)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var request = "";
                    while (!request.Contains("\r\n\r\n"))
                    {
                        var count = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (count == 0) break;
                        request += Encoding.ASCII.GetString(buffer, 0, count);
                    }
                    lock (requests) requests.Add(request);
                    var reply = Encoding.ASCII.GetBytes("ICY 200 OK\r\nicy-name:Radio\r\ncontent-type:audio/mpeg\r\n\r\nAUDIO");
                    await stream.WriteAsync(reply, 0, reply.Length);
                }
            });
            try
            {
                var url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/stream?x=1";
                var response = await TuneInPlaybackHttp.SendAsync(new HttpRequestOptions
                    { Url = url, CancellationToken = CancellationToken.None }, address => IPAddress.IsLoopback(address));
                using (response.Content)
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("audio/mpeg", response.ContentType);
                    var body = new MemoryStream();
                    await response.Content.CopyToAsync(body).WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal("AUDIO", Encoding.ASCII.GetString(body.ToArray()));
                }
                await server.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, requests.Count);
                Assert.All(requests, request => Assert.Contains("User-Agent: " + TuneInPlaybackHttp.UserAgent, request));
                Assert.StartsWith("GET /stream?x=1 HTTP/1.0\r\n", requests[1]);
                Assert.Contains("Icy-MetaData: 0", requests[1]);
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public async Task LegacyShoutcastFallbackRefusesNonPublicAddresses()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var reply = Encoding.ASCII.GetBytes("ICY 200 OK\r\n\r\n");
                await client.GetStream().WriteAsync(reply, 0, reply.Length);
            });
            try
            {
                var url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                var error = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => TuneInPlaybackHttp.SendAsync(
                    new HttpRequestOptions { Url = url, CancellationToken = CancellationToken.None }));
                Assert.Contains("no permitted address", error.Message);
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public async Task HttpClientPathRefusesHostnamesResolvingToNonPublicAddressesBeforeConnecting()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                Assert.True(TuneInPlaybackHttp.PinnedConnections);
                var error = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => TuneInPlaybackHttp.SendAsync(
                    new HttpRequestOptions { Url = url, CancellationToken = CancellationToken.None }));
                Assert.Contains("no permitted address", error.ToString());
                Assert.False(listener.Pending());
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public async Task HttpClientPathConnectsToPermittedAddressesAndReturnsRedirectsUnfollowed()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var request = "";
                while (!request.Contains("\r\n\r\n"))
                {
                    var count = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    request += Encoding.ASCII.GetString(buffer, 0, count);
                }
                var reply = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 302 Found\r\nLocation: http://10.0.0.1/secret\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(reply, 0, reply.Length);
            });
            try
            {
                var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
                var response = await TuneInPlaybackHttp.SendAsync(new HttpRequestOptions
                    { Url = url, CancellationToken = CancellationToken.None }, address => IPAddress.IsLoopback(address));
                using (response.Content)
                {
                    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                    Assert.Equal("http://10.0.0.1/secret", response.Headers["Location"]);
                }
                await server.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { listener.Stop(); }
        }

        [Theory]
        [InlineData("192.0.2.1")]
        [InlineData("198.18.0.1")]
        [InlineData("203.0.113.9")]
        [InlineData("2001:db8::1")]
        [InlineData("2002:0a00:0001::1")]
        [InlineData("::ffff:127.0.0.1")]
        public void ReservedAndEmbeddedPrivateAddressesAreNotPublic(string address) =>
            Assert.False(TuneInPlayback.IsPublicAddress(IPAddress.Parse(address)));

        [Fact]
        public void IcyHeadersParseRedirectsAndRejectInvalidStatusLines()
        {
            var redirect = TuneInPlaybackHttp.ParseIcyHeaders("HTTP/1.0 302 Found\nLocation: http://radio.example/live\n");
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.Equal("http://radio.example/live", redirect.Headers["Location"]);
            Assert.Throws<System.Net.Http.HttpRequestException>(() => TuneInPlaybackHttp.ParseIcyHeaders("<html>\r\n"));
        }

        [Fact]
        public async Task SharedPacerDoesNotScheduleNegativeDelayWhenClockCrossesDeadline()
        {
            var start = DateTime.UtcNow;
            var ticks = 0;
            var pacer = new TuneInRequestPacer(() => start.AddMilliseconds(++ticks * 600), (delay, token) =>
            {
                Assert.True(delay >= TimeSpan.Zero);
                return Task.CompletedTask;
            });
            using (await pacer.SendAsync(() => Task.FromResult(Text("first")), 1, CancellationToken.None)) { }
            using (await pacer.SendAsync(() => Task.FromResult(Text("second")), 1, CancellationToken.None)) { }
        }

        [Fact]
        public async Task SharedCooldownPrioritizesTwoPlaybackStartsThenLetsCatalogueProgress()
        {
            long ticks = DateTime.UtcNow.Ticks;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var starts = new List<string>();
            var pacer = new TuneInRequestPacer(() => new DateTime(Interlocked.Read(ref ticks)), async (wait, token) =>
            {
                var wasBlocked = !release.Task.IsCompleted;
                await release.Task.WaitAsync(token);
                if (!wasBlocked) Interlocked.Add(ref ticks, wait.Ticks);
            });
            using (await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
            {
                StatusCode = HttpStatusCode.ServiceUnavailable,
                Headers = new Dictionary<string, string> { ["Retry-After"] = "75" }
            }), 1, CancellationToken.None, catalogueRequest: true)) { }
            Task<HttpResponseInfo> Queue(string name, bool catalogue = false, CancellationToken token = default) =>
                pacer.SendAsync(() =>
                {
                    starts.Add(name);
                    return Task.FromResult(Text(""));
                }, 1, token, catalogueRequest: catalogue);

            var catalog = Queue("catalogue", true);
            var one = Queue("one");
            using var cancel = new CancellationTokenSource();
            var cancelled = Queue("cancelled", token: cancel.Token);
            var two = Queue("two");
            var three = Queue("three");
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(starts);
            Interlocked.Add(ref ticks, TimeSpan.FromSeconds(75).Ticks);
            release.SetResult(true);
            foreach (var response in await Task.WhenAll(catalog, one, two, three).WaitAsync(TimeSpan.FromSeconds(5)))
                response.Dispose();
            Assert.Equal(new[] { "one", "two", "catalogue", "three" }, starts);
        }

        [Fact]
        public async Task PlaybackCanStartWithoutPreemptingInFlightCatalogue()
        {
            var held = new TaskCompletionSource<HttpResponseInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            long ticks = DateTime.UtcNow.Ticks;
            var pacer = new TuneInRequestPacer(() => new DateTime(Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond)));
            var catalog = pacer.SendAsync(() => held.Task, 1, CancellationToken.None, catalogueRequest: true);
            using (await pacer.SendAsync(() => Task.FromResult(Text("")), 1, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5))) { }
            Assert.False(catalog.IsCompleted);
            held.SetResult(Text(""));
            using (await catalog) { }
        }

        [Theory]
        [InlineData("presets")]
        [InlineData("local")]
        [InlineData("music")]
        [InlineData("language")]
        [InlineData("location")]
        [InlineData("talk")]
        [InlineData("sports")]
        [InlineData("podcast")]
        public async Task EveryCategoryExposesOneScopedHandoffInsteadOfTheAd(string category)
        {
            var fixture = new Fixture(Ad + "?campaign=KeepCase\n" + Live, category);
            var host = Host();
            var channel = new TuneInChannel(fixture.Directory, fixture.Client.Object, Mock.Of<ILogger>(), host.Object);
            var source = Assert.Single(await channel.GetChannelItemMediaInfo(fixture.Id, CancellationToken.None));
            Assert.StartsWith("http://127.0.0.1:8096/emby/TuneIn/Playback/", source.Path);
            Assert.Contains("/" + Uri.EscapeDataString(fixture.Id) + "/", source.Path);
            Assert.Equal(32, new Uri(source.Path).Segments.Last().Length);
            Assert.StartsWith("tunein-preroll-v1-", source.Id);
            Assert.False(source.SupportsDirectPlay);
            Assert.False(source.SupportsDirectStream);
            Assert.True(source.SupportsTranscoding);
            Assert.Null(source.Container);
            Assert.Null(source.RunTimeTicks);
            var request = Assert.Single(fixture.Requests);
            Assert.StartsWith("https://opml.radiotime.com/Tune.ashx?", request.Url);
            var parameters = HttpUtility.ParseQueryString(new Uri(request.Url).Query);
            Assert.Equal(category == "presets" ? "SavedUser" : null, parameters["username"]);
            Assert.Equal("s269846", parameters["id"]);
            Assert.False(request.BufferContent);
        }

        [Fact]
        public async Task OrdinaryStationsUseTheValidatedRelayAndDoNotOpenAudio()
        {
            var fixture = new Fixture(Live + "\nhttps://radio.example/Other.mp3");
            var source = Assert.Single(await fixture.Playback.GetSourcesAsync(fixture.Id, Host().Object, CancellationToken.None));
            Assert.StartsWith("http://127.0.0.1:8096/emby/TuneIn/Playback/", source.Path);
            Assert.StartsWith("tunein-relay-v1-", source.Id);
            Assert.False(source.SupportsDirectPlay);
            Assert.True(source.SupportsTranscoding);
            Assert.Equal(new[] { Live, "https://radio.example/Other.mp3" },
                await fixture.Playback.ResolveStreamsAsync(fixture.Id, CancellationToken.None));
            Assert.DoesNotContain(fixture.Requests, request => request.Url == Live);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task HlsOnlyStationsRequireTheDirectHlsSetting(bool allow)
        {
            var fixture = new Fixture("https://radio.example/live.m3u8");
            fixture.Config.AllowDirectHlsPlayback = allow;
            if (!allow)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    fixture.Playback.GetSourcesAsync(fixture.Id, Host().Object, CancellationToken.None));
                Assert.Contains("Allow direct HLS playback", error.Message);
                return;
            }
            var source = Assert.Single(await fixture.Playback.GetSourcesAsync(fixture.Id, Host().Object, CancellationToken.None));
            Assert.Equal("https://radio.example/live.m3u8", source.Path);
            Assert.Equal("hls", source.Container);
        }

        [Fact]
        public async Task MixedHlsStationsRelayOnlyTheNonHlsStreams()
        {
            var fixture = new Fixture("https://radio.example/live.m3u8\n" + Live);
            var source = Assert.Single(await fixture.Playback.GetSourcesAsync(fixture.Id, Host().Object, CancellationToken.None));
            Assert.StartsWith("tunein-relay-v1-", source.Id);
            using var audio = await fixture.Playback.OpenAsync(fixture.Id, null, CancellationToken.None,
                new Uri(source.Path).Segments.Last());
            Assert.Equal(Live, fixture.Requests.Last().Url);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task UnavailableOrMalformedNestedPlaylistDoesNotDiscardValidatedSibling(bool unavailable)
        {
            var fixture = new Fixture("https://radio.example/broken.pls\n" + Live);
            fixture.Reply = _ => unavailable ? Text("", status: HttpStatusCode.BadGateway)
                : Text("[playlist]\nFile1=ftp://private.example/secret");
            var source = Assert.Single(await fixture.Playback.ResolveStreamsAsync(fixture.Id, CancellationToken.None));
            Assert.Equal(Live, source);
            Assert.DoesNotContain("private.example", Assert.Single(fixture.Warnings));
            Assert.Equal(2, fixture.Requests.Count);
        }

        [Fact]
        public async Task MalformedNestedPlaylistThatAdvertisesRequiredAdDoesNotFallThrough()
        {
            var fixture = new Fixture("https://radio.example/broken.pls\n" + Live);
            fixture.Reply = _ => Text("[playlist]\nFile1=" + Ad + "\nFile2=ftp://private.example/live");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Playback.GetSourcesAsync(fixture.Id, Host().Object, CancellationToken.None));
            Assert.Equal(2, fixture.Requests.Count);
        }

        [Theory]
        [InlineData("#STATUS: 400\n# secret content", "HTTP 200 with playlist status 400 and no URLs")]
        [InlineData("# secret content", "empty playback playlist")]
        public async Task ProviderPlaylistErrorsAreDistinctAndRedacted(string metadata, string expected)
        {
            var fixture = new Fixture(metadata);
            var error = await Record.ExceptionAsync(() =>
                fixture.Playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None));
            Assert.Contains(expected, error.Message);
            Assert.DoesNotContain("secret", Assert.Single(fixture.Warnings));
        }

        [Theory]
        [InlineData("queue", "queue wait")]
        [InlineData("opening", "network opening")]
        [InlineData("body", "playlist body read")]
        public async Task TimeoutNamesTheStage(string stage, string expected)
        {
            using var openings = new SemaphoreSlim(0, 1);
            var fixture = new Fixture("https://radio.example/list.pls");
            fixture.Reply = _ => stage == "opening" ? throw new TimeoutException("private URL")
                : new HttpResponseInfo { StatusCode = HttpStatusCode.OK, Content = new WaitingStream() };
            var playback = new TuneInPlayback(fixture.Directory, fixture.Client.Object, fixture.Warnings.Add,
                new TuneInRequestPacer(), queueTimeout: stage == "queue" ? TimeSpan.FromMilliseconds(100) : (TimeSpan?)null,
                playlistTimeout: stage == "body" ? TimeSpan.Zero : (TimeSpan?)null,
                externalOpenings: stage == "queue" ? openings : null);
            var error = await Record.ExceptionAsync(() => playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None));
            Assert.Contains(expected, error.Message);
            Assert.All(fixture.Warnings, warning => Assert.DoesNotContain("private URL", warning));
        }

        [Fact]
        public async Task NestedRedirectToPrivateDestinationIsNotSent()
        {
            var fixture = new Fixture("https://radio.example/list.m3u\n" + Live);
            fixture.Reply = _ => new HttpResponseInfo
            {
                StatusCode = HttpStatusCode.Redirect, Content = Stream.Null,
                Headers = new Dictionary<string, string> { ["Location"] = "http://127.0.0.1/secret" }
            };
            var source = Assert.Single(await fixture.Playback.ResolveStreamsAsync(fixture.Id, CancellationToken.None));
            Assert.Equal(Live, source);
            Assert.Equal(2, fixture.Requests.Count);
        }

        [Fact]
        public async Task NestedRelativeEntriesUseTheValidatedRedirectDestination()
        {
            var fixture = new Fixture("https://radio.example/list.m3u");
            fixture.Reply = request => request.Url == "https://radio.example/list.m3u"
                ? new HttpResponseInfo
                {
                    StatusCode = HttpStatusCode.Redirect, Content = Stream.Null,
                    Headers = new Dictionary<string, string> { ["Location"] = "https://mirror.example/other/list.m3u" }
                }
                : Text("live.mp3");
            var source = Assert.Single(await fixture.Playback.ResolveStreamsAsync(fixture.Id, CancellationToken.None));
            Assert.Equal("https://mirror.example/other/live.mp3", source);
        }

        [Fact]
        public async Task NestedPlaylistSizeLimitIsNotBypassedBySiblingFallback()
        {
            var fixture = new Fixture("https://radio.example/list.m3u\n" + Live);
            fixture.Reply = _ => Text(new string('x', 256 * 1024 + 1));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None));
            Assert.Contains("size limit", error.Message);
        }

        [Fact]
        public async Task LoopbackAttemptReusesNestedPlanOnceAndExternalOpeningsIgnoreProviderCooldown()
        {
            var fixture = new Fixture("https://radio.example/list.pls");
            fixture.Reply = request => request.Url.EndsWith("list.pls")
                ? Text("[playlist]\nFile1=" + Ad + "\nFile2=" + Live)
                : Text("audio", "audio/mpeg");
            var now = DateTime.UtcNow;
            var pacer = new TuneInRequestPacer(() => now, (delay, _) => Task.Delay(Timeout.InfiniteTimeSpan, _));
            var playback = new TuneInPlayback(fixture.Directory, fixture.Client.Object, fixture.Warnings.Add, pacer);
            var media = Assert.Single(await playback.GetSourcesAsync(fixture.Id, Host().Object, CancellationToken.None));
            var handle = new Uri(media.Path).Segments.Last();
            now = now.AddSeconds(3);
            using (await pacer.SendAsync(() => Task.FromResult(new HttpResponseInfo
            {
                StatusCode = HttpStatusCode.TooManyRequests,
                Headers = new Dictionary<string, string> { ["Retry-After"] = "75" }
            }), 1, CancellationToken.None, catalogueRequest: true)) { }

            var servicePlayback = new TuneInPlayback(fixture.Directory, fixture.Client.Object, fixture.Warnings.Add,
                pacer, queueTimeout: TimeSpan.FromSeconds(1));
            using (var source = await servicePlayback.OpenAsync(fixture.Id, Own, CancellationToken.None, handle))
                Assert.Equal("audioaudio", Encoding.ASCII.GetString((await Bytes(source.Body)).Skip(44).ToArray()));
            Assert.Equal(4, fixture.Requests.Count);
            now = now.AddMinutes(2);
            using (await servicePlayback.OpenAsync(fixture.Id, Own, CancellationToken.None, handle)) { }
            Assert.Equal(2, fixture.Requests.Count(request => request.Url.Contains("Tune.ashx")));
            Assert.Equal(2, fixture.Requests.Count(request => request.Url.EndsWith("list.pls")));
        }

        [Fact]
        public void AttemptHandlesAreScopedExpiringAndBounded()
        {
            var now = DateTime.UtcNow;
            var attempts = new TuneInPlaybackAttempts<object>(() => now, capacity: 1);
            var fixture = new Fixture(Live);
            var scope = fixture.Directory.Snapshot().Key;
            var plan = new object();
            var first = attempts.Add(fixture.Directory, scope, fixture.Id, plan);
            Assert.Throws<InvalidOperationException>(() => attempts.Take(fixture.Directory, scope, "other", first));
            Assert.Throws<InvalidOperationException>(() => attempts.Take(fixture.Directory, "other", fixture.Id, first));
            Assert.Same(plan, attempts.Take(fixture.Directory, scope, fixture.Id, first));
            Assert.Null(attempts.Take(fixture.Directory, scope, fixture.Id, first));
            var evicted = attempts.Add(fixture.Directory, scope, fixture.Id, plan);
            var expires = attempts.Add(fixture.Directory, scope, fixture.Id, plan);
            Assert.NotEqual(evicted, expires);
            Assert.Null(attempts.Take(fixture.Directory, scope, fixture.Id, evicted));
            now = now.AddSeconds(30);
            Assert.Null(attempts.Take(fixture.Directory, scope, fixture.Id, expires));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AdThenOneLiveAlternativeProducesContinuousWaveAndPreservesFavorites(bool nested)
        {
            var fixture = new Fixture(nested ? "https://radio.example/list.pls" : Ad + "\n" + Live
                + "\nhttps://radio.example/unused");
            fixture.Reply = request =>
            {
                if (new Uri(request.Url).AbsolutePath == "/list.pls")
                    return Text("[playlist]\nFile2=" + Live + "\nFile1=" + Ad + "\nNumberOfEntries=2", "audio/x-scpls");
                return Text(TuneInPlayback.IsPreRoll(request.Url) ? "AD" : "LIVE",
                    TuneInPlayback.IsPreRoll(request.Url) ? "audio/mpeg" : "audio/aac");
            };
            using (var source = await fixture.Playback.OpenAsync(fixture.Id, Own, CancellationToken.None))
            {
                Assert.Equal("audio/wav", source.ContentType);
                var bytes = await Bytes(source.Body);
                Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
                Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));
                Assert.Equal(2, BitConverter.ToInt16(bytes, 22));
                Assert.Equal("ADLIVE", Encoding.ASCII.GetString(bytes, 44, bytes.Length - 44));
            }
            Assert.DoesNotContain(fixture.Requests, request => request.Url.EndsWith("/unused"));
            Assert.All(fixture.Requests, request => Assert.False(request.BufferContent));
            Assert.All(fixture.Streams, stream => Assert.True(stream.Disposed));
            Assert.Contains("username=SavedUser", fixture.Requests[0].Url);
        }

        [Fact]
        public async Task ProbeIsSeparateAndDoesNotConsumeOrOpenTheLiveStage()
        {
            var fixture = new Fixture(Ad + "\n" + Live);
            using (var probe = await fixture.Playback.OpenAsync(fixture.Id, Own, CancellationToken.None))
            {
                var header = new byte[44];
                await probe.Body.ReadExactlyAsync(header);
                Assert.Equal("WAVE", Encoding.ASCII.GetString(header, 8, 4));
            }
            Assert.Equal(2, fixture.Requests.Count);
            Assert.All(fixture.Streams, stream => Assert.True(stream.Disposed));
            using (var playback = await fixture.Playback.OpenAsync(fixture.Id, Own, CancellationToken.None))
                Assert.Equal("ADLIVE", Encoding.ASCII.GetString((await Bytes(playback.Body)).Skip(44).ToArray()));
            Assert.Equal(5, fixture.Requests.Count);
            Assert.Equal(2, fixture.Requests.Count(request => request.Url.StartsWith("https://opml.")));
        }

        [Fact]
        public async Task MultipleAdsFinishBeforeLiveAndFailedLiveOpeningDoesNotRepeatThem()
        {
            var fixture = new Fixture(Ad + "\n" + Ad + "\nhttps://radio.example/dead\n" + Live);
            fixture.Reply = request => request.Url.EndsWith("/dead")
                ? Text("", "audio/aac", HttpStatusCode.ServiceUnavailable)
                : Text(TuneInPlayback.IsPreRoll(request.Url) ? "AD" : "LIVE",
                    TuneInPlayback.IsPreRoll(request.Url) ? "audio/mpeg" : "audio/aac");
            using (var source = await fixture.Playback.OpenAsync(fixture.Id, Own, CancellationToken.None))
                Assert.Equal("ADADLIVE", Encoding.ASCII.GetString((await Bytes(source.Body)).Skip(44).ToArray()));
            Assert.Equal(2, fixture.Requests.Count(request => TuneInPlayback.IsPreRoll(request.Url)));
            Assert.Single(fixture.Warnings);
            Assert.DoesNotContain("https://", fixture.Warnings[0]);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task FailedRequiredAdOrDecoderNeverSkipsToLive(bool decoderFails)
        {
            var fixture = new Fixture(Ad + "\n" + Live);
            if (!decoderFails) fixture.Reply = _ => Text("", "audio/mpeg", HttpStatusCode.Forbidden);
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Playback.OpenAsync(fixture.Id,
                (_, __) => throw new InvalidOperationException("Decoder unavailable."), CancellationToken.None));
            Assert.DoesNotContain(fixture.Requests, request => request.Url == Live);
            Assert.All(fixture.Streams, stream => Assert.True(stream.Disposed));
        }

        [Fact]
        public async Task FailedLiveReadIsTerminalAndDoesNotTryAnotherStationAlternative()
        {
            var fixture = new Fixture(Ad + "\n" + Live + "\nhttps://radio.example/unused");
            fixture.Reply = request => request.Url == Live
                ? new HttpResponseInfo { StatusCode = HttpStatusCode.OK, ContentType = "audio/aac", Content = new BrokenStream() }
                : Text("AD", "audio/mpeg");
            using (var source = await fixture.Playback.OpenAsync(fixture.Id, Own, CancellationToken.None))
                await Assert.ThrowsAsync<IOException>(() => Bytes(source.Body));
            Assert.DoesNotContain(fixture.Requests, request => request.Url.EndsWith("/unused"));
            Assert.Contains(fixture.Warnings, warning => warning.Contains("playback failed"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TamperedOrDisabledSignedItemsCannotOpenRelay(bool disable)
        {
            var fixture = new Fixture(Ad + "\n" + Live);
            if (disable) fixture.Config.EnableFavorites = false;
            var id = disable ? fixture.Id : "X" + fixture.Id.Substring(1);
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Playback.OpenAsync(id, Own, CancellationToken.None));
            Assert.Empty(fixture.Requests);
        }

        [Theory]
        [InlineData("music", true)]
        [InlineData("username", false)]
        public async Task SaveKeepsPlayingOnlyStationsTheNewSettingsStillAllow(string change, bool keeps)
        {
            var fixture = new Fixture(Ad + "\n" + Live);
            using (var source = await fixture.Playback.OpenAsync(fixture.Id, Own, CancellationToken.None))
            {
                await source.Body.ReadExactlyAsync(new byte[44]);
                if (change == "music") fixture.Config.EnableMusic = false;
                else fixture.Config.Username = "OtherUser";
                TuneInPlayback.CancelChangedScopes();
                if (keeps)
                {
                    await Bytes(source.Body);
                    Assert.Contains(fixture.Requests, request => request.Url == Live);
                }
                else
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bytes(source.Body));
                    Assert.DoesNotContain(fixture.Requests, request => request.Url == Live);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ScopeChangeOrCancellationClosesAdAndPreventsDeferredLive(bool changeScope)
        {
            var fixture = new Fixture(Ad + "\n" + Live);
            using (var caller = new CancellationTokenSource())
            using (var source = await fixture.Playback.OpenAsync(fixture.Id, Own, caller.Token))
            {
                await source.Body.ReadExactlyAsync(new byte[44]);
                if (changeScope)
                {
                    fixture.Config.EnableFavorites = false;
                    TuneInPlayback.CancelChangedScopes();
                }
                else caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bytes(source.Body));
            }
            Assert.DoesNotContain(fixture.Requests, request => request.Url == Live);
            Assert.All(fixture.Streams, stream => Assert.True(stream.Disposed));
        }

        [Theory]
        [InlineData("http://radio.example/ad.mp3")]
        [InlineData("https://tunein-ondemand.cdnstream1.com/other.mp3")]
        [InlineData("https://tunein-ondemand.cdnstream1.com.evil.example/multi_bump_sonic_pre_pre.mp3")]
        public async Task ArbitraryFiniteAudioIsNotClassifiedAsPreRoll(string url)
        {
            var fixture = new Fixture(url + "\n" + Live);
            var sources = await fixture.Playback.ResolveStreamsAsync(fixture.Id, CancellationToken.None);
            Assert.Equal(new[] { url, Live }, sources);
        }

        [Fact]
        public async Task InvalidOrderMissingLiveAndOversizedListsFailBeforeAudioOpening()
        {
            foreach (var list in new[] { Ad, Live + "\n" + Ad, string.Join("\n", Enumerable.Repeat(Live, 9)) })
            {
                var fixture = new Fixture(list);
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    fixture.Playback.GetSourcesAsync(fixture.Id, Host().Object, CancellationToken.None));
                Assert.Single(fixture.Requests);
                Assert.All(fixture.Streams, stream => Assert.True(stream.Disposed));
            }
        }

        [Fact]
        public async Task M3uCommentsAndRelativeEntriesPreserveNativeHlsAndUrlCase()
        {
            var fixture = new Fixture("https://radio.example/Path/list.m3u");
            fixture.Reply = _ => Text("#EXTM3U\n#EXTINF:-1,Radio\nLive.AAC?Signature=AbC\n", "audio/x-mpegurl");
            var source = Assert.Single(await fixture.Playback.ResolveStreamsAsync(fixture.Id, CancellationToken.None));
            Assert.Equal("https://radio.example/Path/Live.AAC?Signature=AbC", source);
            fixture.Metadata = "https://radio.example/live.m3u8";
            fixture.Config.AllowDirectHlsPlayback = true;
            var hls = Assert.Single(await fixture.Playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None));
            Assert.Equal("https://radio.example/live.m3u8", hls.Path);
            Assert.Equal("hls", hls.Container);
            Assert.Equal(3, fixture.Requests.Count);
        }

        [Fact]
        public async Task PlaylistCycleFailsBoundedlyInsteadOfRecursing()
        {
            var fixture = new Fixture("https://radio.example/list.pls");
            fixture.Reply = _ => Text("[playlist]\nFile1=https://radio.example/list.pls");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None));
            Assert.Equal(2, fixture.Requests.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CallerCancellationOrScopeSaveInterruptsBlockedLiveReading(bool changeScope)
        {
            var fixture = new Fixture(Ad + "\n" + Live);
            var blocked = new WaitingStream();
            fixture.Reply = request => request.Url == Live
                ? new HttpResponseInfo { StatusCode = HttpStatusCode.OK, ContentType = "audio/aac", Content = blocked }
                : Text("AD", "audio/mpeg");
            using (var caller = new CancellationTokenSource())
            using (var source = await fixture.Playback.OpenAsync(fixture.Id, Own, caller.Token))
            {
                var reading = Bytes(source.Body);
                await blocked.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (changeScope)
                {
                    fixture.Config.EnableFavorites = false;
                    TuneInPlayback.CancelChangedScopes();
                }
                else caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            Assert.True(blocked.Disposed);
            Assert.All(fixture.Streams, stream => Assert.True(stream.Disposed));
        }

        [FfmpegFact]
        public async Task RealDecoderFailureAndBlockedInputCancellationReleaseResources()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            using (var decoder = new PcmDecoderStream(encoder,
                new TuneInAudioSource(new MemoryStream(new byte[128]), "audio/mpeg"), CancellationToken.None))
                await Assert.ThrowsAsync<InvalidDataException>(() => Bytes(decoder));
            var blocked = new WaitingStream();
            using (var caller = new CancellationTokenSource())
            using (var decoder = new PcmDecoderStream(encoder, new TuneInAudioSource(blocked, "audio/aac"), caller.Token))
            {
                var reading = decoder.ReadAsync(new byte[1024], 0, 1024);
                await blocked.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            Assert.True(blocked.Disposed);
        }

        [Theory]
        [InlineData("192.0.2.1", true)]
        [InlineData("127.0.0.1", false)]
        [InlineData("::1", true)]
        [InlineData("::ffff:127.0.0.1", true)]
        public async Task HandoffServiceRequiresAnActualLocalLoopbackSocket(string address, bool local)
        {
            var fixture = new Fixture(Ad + "\n" + Live);
            var request = Request(address, local);
            var service = new TuneInPlaybackService(fixture.Playback, Mock.Of<IHttpResultFactory>(), null,
                Mock.Of<ILogger>()) { Request = request.Object };
            if (!local || !IPAddress.IsLoopback(IPAddress.Parse(address).MapToIPv6().IsIPv4MappedToIPv6
                ? IPAddress.Parse(address).MapToIPv4() : IPAddress.Parse(address)))
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    service.Get(new GetTuneInPlayback { Id = fixture.Id }));
                Assert.Empty(fixture.Requests);
            }
            else
            {
                // A local peer still cannot bypass the signed-item authorization.
                await Assert.ThrowsAnyAsync<Exception>(() => service.Get(new GetTuneInPlayback { Id = "s269846" }));
                Assert.Empty(fixture.Requests);
            }
        }

        [Fact]
        public async Task FactoryFailureClosesTheStreamAlreadyOpenedByTheService()
        {
            var fixture = new Fixture(Live);
            var factory = DispatchProxy.Create<IHttpResultFactory, ResultProxy>();
            ((ResultProxy)(object)factory).Fail = true;
            var service = new TuneInPlaybackService(fixture.Playback, factory, null,
                Mock.Of<ILogger>()) { Request = Request("127.0.0.1", true).Object };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.Get(new GetTuneInPlayback { Id = fixture.Id }));
            Assert.All(fixture.Streams, stream => Assert.True(stream.Disposed));
            Assert.True(((ResultProxy)(object)factory).Called);
        }

        [FfmpegFact]
        public async Task RealMixedCodecHandoffAndHostDecoderAreUsedWhenFfmpegIsAvailable()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            Assert.False(string.IsNullOrEmpty(encoder), "Set TUNEIN_TEST_FFMPEG to run the real codec handoff regression.");
            var mp3 = await Encode(encoder, "-f lavfi -i sine=frequency=440:duration=0.15 -ar 22050 -ac 1 -f mp3 pipe:1");
            var aac = await Encode(encoder, "-f lavfi -i sine=frequency=880:duration=0.15 -ar 48000 -ac 2 -f adts pipe:1");
            var fixture = new Fixture(Ad + "\n" + Live);
            fixture.Reply = request => new HttpResponseInfo
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = TuneInPlayback.IsPreRoll(request.Url) ? "audio/mpeg" : "audio/aac",
                Content = new TrackedStream(TuneInPlayback.IsPreRoll(request.Url) ? mp3 : aac)
            };
            var factory = DispatchProxy.Create<IHttpResultFactory, ResultProxy>();
            var ffmpeg = new Mock<IFfmpegManager>();
            ffmpeg.SetupGet(manager => manager.FfmpegConfiguration.EncoderPath).Returns(encoder);
            var service = new TuneInPlaybackService(fixture.Playback, factory, ffmpeg.Object,
                Mock.Of<ILogger>()) { Request = Request("127.0.0.1", true).Object };
            byte[] wave;
            using (var body = Assert.IsAssignableFrom<Stream>(await service.Get(new GetTuneInPlayback { Id = fixture.Id })))
                wave = await Bytes(body);
            byte[] expectedAd;
            byte[] expectedLive;
            using (var decoder = new PcmDecoderStream(encoder, new TuneInAudioSource(new MemoryStream(mp3), "audio/mpeg"),
                CancellationToken.None)) expectedAd = await Bytes(decoder);
            using (var decoder = new PcmDecoderStream(encoder, new TuneInAudioSource(new MemoryStream(aac), "audio/aac"),
                CancellationToken.None)) expectedLive = await Bytes(decoder);
            Assert.NotEmpty(expectedAd);
            Assert.NotEmpty(expectedLive);
            Assert.Equal(expectedAd.Concat(expectedLive), wave.Skip(44));
            using (var reprobe = new PcmDecoderStream(encoder,
                new TuneInAudioSource(new MemoryStream(wave), "audio/wav"), CancellationToken.None))
                Assert.Equal(wave.Skip(44), await Bytes(reprobe));
            ffmpeg.VerifyGet(manager => manager.FfmpegConfiguration, Times.AtLeastOnce);
            Assert.All(fixture.Streams, stream => Assert.True(stream.Disposed));
        }

        public sealed class FfmpegFactAttribute : FactAttribute
        {
            public FfmpegFactAttribute()
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG")))
                    Skip = "Set TUNEIN_TEST_FFMPEG to run the real mixed-codec regression.";
            }
        }

        public class ResultProxy : DispatchProxy
        {
            public bool Fail;
            public bool Called;
            protected override object Invoke(MethodInfo method, object[] arguments)
            {
                Assert.Equal("GetResult", method.Name);
                Called = true;
                Assert.IsAssignableFrom<Stream>(arguments[1]);
                if (Fail) throw new InvalidOperationException("Result creation failed.");
                Assert.Equal("audio/wav", arguments[2]);
                return arguments[1];
            }
        }

        private static Mock<IServerApplicationHost> Host()
        {
            var host = new Mock<IServerApplicationHost>();
            host.Setup(value => value.GetLocalApiUrl(IPAddress.Loopback)).Returns("http://127.0.0.1:8096/emby");
            return host;
        }

        private static Mock<IRequest> Request(string address, bool local)
        {
            var request = new Mock<IRequest>();
            request.SetupGet(value => value.RemoteIp).Returns(IPAddress.Parse(address));
            request.SetupGet(value => value.IsSocketConnectionLocal).Returns(local);
            return request;
        }

        private static HttpResponseInfo Text(string text, string type = "text/plain", HttpStatusCode status = HttpStatusCode.OK) =>
            new HttpResponseInfo { StatusCode = status, ContentType = type, Content = new TrackedStream(Encoding.UTF8.GetBytes(text)) };

        private static Stream Own(TuneInAudioSource source, CancellationToken token) => new OwnedStream(source, token);

        private static async Task<byte[]> Bytes(Stream source)
        {
            using (var memory = new MemoryStream())
            {
                await source.CopyToAsync(memory);
                return memory.ToArray();
            }
        }

        private static async Task<byte[]> Encode(string encoder, string arguments)
        {
            using (var process = Process.Start(new ProcessStartInfo(encoder, "-hide_banner -loglevel error " + arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            using (timeout.Token.Register(() => { if (!process.HasExited) process.Kill(); }))
            {
                var errors = process.StandardError.ReadToEndAsync();
                var output = Bytes(process.StandardOutput.BaseStream);
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await errors);
                return await output;
            }
        }

        private sealed class Fixture
        {
            internal readonly PluginConfiguration Config;
            internal readonly TuneInDirectory Directory;
            internal readonly Mock<IHttpClient> Client = new Mock<IHttpClient>(MockBehavior.Strict);
            internal readonly List<HttpRequestOptions> Requests = new List<HttpRequestOptions>();
            internal readonly List<TrackedStream> Streams = new List<TrackedStream>();
            internal readonly List<string> Warnings = new List<string>();
            internal readonly TuneInPlayback Playback;
            internal readonly string Id;
            internal string Metadata;
            internal Func<HttpRequestOptions, HttpResponseInfo> Reply = request =>
                Text(TuneInPlayback.IsPreRoll(request.Url) ? "AD" : "LIVE",
                    TuneInPlayback.IsPreRoll(request.Url) ? "audio/mpeg" : "audio/aac");
            internal Fixture(string metadata, string category = "presets")
            {
                var crypto = new Mock<ICryptoProvider>();
                crypto.Setup(provider => provider.GetMD5(It.IsAny<string>())).Returns((string text) =>
                    new Guid(MD5.HashData(Encoding.UTF8.GetBytes(text))));
                BaseExtensions.CryptographyProvider = crypto.Object;
                Metadata = metadata;
                Config = new PluginConfiguration
                {
                    SchemaVersion = 1, ConfigurationCompleted = true, EnableFavorites = true, Username = "SavedUser",
                    EnableLocalRadio = true, LocalRadioSource = "coordinates", UseCoordinates = true, LatLon = "0,0",
                    EnableMusic = true, Music = new List<CatalogSelection> { Selection("g1") },
                    EnableLanguage = true, Languages = new List<CatalogSelection> { Selection("l1") },
                    EnableLocation = true, Locations = new List<CatalogSelection> { Selection("r1") },
                    EnableTalk = true, EnableSports = true, EnablePodcasts = true, ApiRequestIntervalSeconds = 1
                };
                Directory = new TuneInDirectory(() => Config, (_, __) => throw new Exception("Unexpected catalog request."));
                var snapshot = Directory.Snapshot();
                Id = Directory.Register(snapshot, new NavigationEntry
                {
                    Category = category, StationId = "s269846",
                    SelectionId = category == "music" ? "g1" : category == "language" ? "l1"
                        : category == "location" ? "r1" : category == "local" ? "coordinates" : null,
                    ContentCategory = category == "language" ? "language" : null
                });
                Client.Setup(value => value.SendAsync(It.IsAny<HttpRequestOptions>(), "GET"))
                    .Returns((HttpRequestOptions request, string method) =>
                    {
                        request.CancellationToken.ThrowIfCancellationRequested();
                        Requests.Add(request);
                        var response = new Uri(request.Url).Host == "opml.radiotime.com" ? Text(Metadata) : Reply(request);
                        if (response.Content is TrackedStream stream) Streams.Add(stream);
                        return Task.FromResult(response);
                    });
                var now = DateTime.UtcNow;
                var pacer = new TuneInRequestPacer(() => now, (wait, _) =>
                {
                    now += wait;
                    return Task.CompletedTask;
                });
                Playback = new TuneInPlayback(Directory, Client.Object, Warnings.Add, pacer);
            }
            private static CatalogSelection Selection(string id) =>
                new CatalogSelection { Id = id, Name = id, Enabled = true };
        }

        private sealed class OwnedStream : RadioReadStream
        {
            private readonly TuneInAudioSource source;
            private readonly CancellationTokenRegistration registration;
            internal OwnedStream(TuneInAudioSource source, CancellationToken token)
            { this.source = source; registration = token.Register(source.Dispose); }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                source.Body.ReadAsync(buffer, offset, count, token);
            protected override void Dispose(bool disposing)
            {
                if (disposing) { registration.Dispose(); source.Dispose(); }
                base.Dispose(disposing);
            }
        }

        private sealed class TrackedStream : MemoryStream
        {
            internal bool Disposed;
            internal TrackedStream(byte[] bytes) : base(bytes) { }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        private sealed class BrokenStream : RadioReadStream
        {
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                throw new IOException("Synthetic live read failure.");
        }

        private sealed class WaitingStream : RadioReadStream
        {
            internal readonly TaskCompletionSource<bool> Reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<int> finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Disposed;
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                Reading.TrySetResult(true);
                return finished.Task;
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                finished.TrySetCanceled();
                base.Dispose(disposing);
            }
        }

        private sealed class StalledSyncStream : RadioReadStream
        {
            private readonly TaskCompletionSource<int> finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            private byte[] target;
            internal bool Disposed;
            protected override TimeSpan SynchronousReadTimeout => TimeSpan.FromMilliseconds(50);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                target = buffer;
                return finished.Task;
            }
            internal void Finish() { target[0] = 9; finished.SetResult(1); }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }
    }
}
