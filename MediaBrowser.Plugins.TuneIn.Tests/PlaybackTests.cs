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
            Assert.EndsWith(fixture.Id, source.Path);
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
        public async Task OrdinaryStationsRetainDirectSourcesAndDoNotOpenAudio()
        {
            var fixture = new Fixture(Live + "\nhttps://radio.example/Other.mp3");
            var sources = (await fixture.Playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None)).ToArray();
            Assert.Equal(new[] { Live, "https://radio.example/Other.mp3" }, sources.Select(source => source.Path));
            Assert.All(sources, source => Assert.True(source.SupportsDirectPlay));
            Assert.Equal("aac", sources[0].Container);
            Assert.Single(fixture.Requests);
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
            var sources = await fixture.Playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None);
            Assert.Equal(new[] { url, Live }, sources.Select(source => source.Path));
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
            var source = Assert.Single(await fixture.Playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None));
            Assert.Equal("https://radio.example/Path/Live.AAC?Signature=AbC", source.Path);
            fixture.Metadata = "https://radio.example/live.m3u8";
            source = Assert.Single(await fixture.Playback.GetSourcesAsync(fixture.Id, null, CancellationToken.None));
            Assert.Equal("https://radio.example/live.m3u8", source.Path);
            Assert.Equal("hls", source.Container);
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
                Playback = new TuneInPlayback(Directory, Client.Object, Warnings.Add);
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
    }
}
