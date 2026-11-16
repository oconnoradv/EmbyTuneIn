using MediaBrowser.Plugins.TuneIn.Configuration;
using MediaBrowser.Plugins.TuneIn.Security;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class PlaybackTests
    {
        private const string PreRoll = "https://tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3";
        private const string Live = "https://radio.test/live";
        private static readonly NavigationEntry Station = new NavigationEntry { Category = "talk", StationId = "s1" };

        [Theory]
        [InlineData("audio/mpeg")]
        [InlineData("audio/aac")]
        public async Task RequiredPreRollPrecedesOneLiveAlternativeInAStableWaveRelay(string liveType)
        {
            var opened = new List<string>();
            var decoded = new List<string>();
            var directory = Directory(PreRoll + "\n" + Live + "\nhttps://radio.test/unused", uri =>
            {
                opened.Add(uri.AbsolutePath);
                return Reply(uri.Host == "radio.test" ? liveType : "audio/mpeg",
                    Encoding.ASCII.GetBytes(uri.Host == "radio.test" ? "LIVE" : "AD"));
            });
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => { }, (source, token) =>
                {
                    decoded.Add(source.ContentType);
                    return new OwnedStream(source);
                }))
            {
                Assert.Equal("audio/wav", response.ContentType);
                Assert.Single(opened);
                Assert.Equal(0, await response.Body.ReadAsync(new byte[1], 0, 0, CancellationToken.None));
                Assert.Single(opened);
                var bytes = await TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None);
                Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
                Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
                Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));
                Assert.Equal("ADLIVE", Encoding.ASCII.GetString(bytes, 44, bytes.Length - 44));
                Assert.Equal(new[] { "audio/mpeg", liveType }, decoded);
                Assert.Equal(new[] { "/multi_bump_sonic_pre_pre.mp3", "/live" }, opened);
            }
        }

        [Fact]
        public async Task RequiredAdTransitionsToLegacyLiveHeadersWithoutEndingTheRelay()
        {
            var opened = 0;
            var directory = Directory(PreRoll + "\n" + Live, uri =>
            {
                opened++;
                return uri.Host == "radio.test"
                    ? new HttpStream("HTTP/1.1 200 OK\nContent-Type: audio/mpeg\nicy-br: 128\nicy-br: 128"
                        + "\nTransfer-Encoding: identity\n\nLIVE")
                    : Reply("audio/mpeg", Encoding.ASCII.GetBytes("AD"));
            });
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => throw new Exception("A compatible live response must not require fallback."),
                (source, _) => new OwnedStream(source)))
            {
                var bytes = await TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None);
                Assert.Equal("ADLIVE", Encoding.ASCII.GetString(bytes, 44, bytes.Length - 44));
            }
            Assert.Equal(2, opened);
        }

        [Fact]
        public async Task KexpStyleLiveAlternativesRemainPassthroughWithoutDecoderOrSequentialPlayback()
        {
            var opened = 0;
            var directory = Directory(Live + "\nhttps://radio.test/unused", _ =>
            {
                opened++;
                return Reply("audio/aac", Encoding.ASCII.GetBytes("AAC"));
            });
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => { }, (_, __) => throw new Exception("No normalization for stations without pre-roll.")))
            {
                Assert.Equal("audio/aac", response.ContentType);
                Assert.Equal("AAC", Encoding.ASCII.GetString(
                    await TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None)));
            }
            Assert.Equal(1, opened);
        }

        [Fact]
        public async Task LiveOpeningFailureAfterAdUsesLiveFallbackButDoesNotRepeatAd()
        {
            var opened = new List<string>();
            var warnings = new List<string>();
            var directory = Directory(PreRoll + "\nhttps://radio.test/dead\nhttps://radio.test/list.pls", uri =>
            {
                opened.Add(uri.AbsolutePath);
                if (uri.AbsolutePath == "/dead") return Reply("text/plain", Array.Empty<byte>(), 503);
                if (uri.AbsolutePath == "/list.pls")
                    return Reply("audio/x-scpls", Encoding.ASCII.GetBytes("[playlist]\nFile1=" + Live));
                return Reply("audio/mpeg", Encoding.ASCII.GetBytes(uri.Host == "radio.test" ? "LIVE" : "AD"));
            });
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                warnings.Add, (source, _) => new OwnedStream(source)))
            {
                var bytes = await TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None);
                Assert.Equal("ADLIVE", Encoding.ASCII.GetString(bytes, 44, bytes.Length - 44));
            }
            Assert.Single(warnings);
            Assert.DoesNotContain("https://", warnings[0]);
            Assert.Equal(new[] { "/multi_bump_sonic_pre_pre.mp3", "/dead", "/list.pls", "/live" }, opened);
        }

        [Fact]
        public async Task TruncatedAdFailsWithoutSkippingRequiredPlaybackAndFailureRemainsTerminal()
        {
            var opened = 0;
            var directory = Directory(PreRoll + "\n" + Live, _ =>
            {
                opened++;
                return new HttpStream("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: 100\r\n\r\nAD");
            });
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => { }, (source, _) => new OwnedStream(source)))
            {
                await Assert.ThrowsAsync<EndOfStreamException>(() =>
                    TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None));
                await Assert.ThrowsAsync<EndOfStreamException>(() =>
                    response.Body.ReadAsync(new byte[1], 0, 1, CancellationToken.None));
            }
            Assert.Equal(1, opened);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CancellationOrConfigurationChangeCannotOpenLiveAfterAd(bool changeConfiguration)
        {
            var config = Config();
            var opened = 0;
            var directory = Directory(PreRoll + "\n" + Live, _ =>
            {
                opened++;
                return Reply("audio/mpeg", new byte[] { 1, 2 });
            }, config);
            var snapshot = directory.Snapshot();
            using (var caller = new CancellationTokenSource())
            using (var operation = directory.Operation(snapshot, caller.Token))
            using (var response = await directory.OpenStreamAsync(snapshot, Station, operation.Token,
                _ => { }, (source, _) => new OwnedStream(source)))
            {
                if (changeConfiguration)
                {
                    config.EnableTalk = false;
                    directory.Snapshot();
                }
                else caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    response.Body.ReadAsync(new byte[44], 0, 44, CancellationToken.None));
            }
            Assert.Equal(1, opened);
        }

        [Fact]
        public async Task PerReadCancellationAndEarlyProbeDisposalDoNotOpenLiveAndReleaseAd()
        {
            HttpStream ad = null;
            var opened = 0;
            var directory = Directory(PreRoll + "\n" + Live, _ =>
            {
                opened++;
                return ad = Reply("audio/mpeg", new byte[] { 1, 2 });
            });
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => { }, (source, _) => new OwnedStream(source)))
            using (var caller = new CancellationTokenSource())
            {
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    response.Body.ReadAsync(new byte[1], 0, 1, caller.Token));
            }
            Assert.True(ad.Disposed);
            Assert.Equal(1, opened);
        }

        [Theory]
        [InlineData("https://tunein-ondemand.cdnstream1.com.evil.test/multi_bump_sonic_pre_pre.mp3")]
        [InlineData("https://tunein-ondemand.cdnstream1.com/other.mp3")]
        [InlineData("http://tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3")]
        [InlineData("https://tunein-ondemand.cdnstream1.com:444/multi_bump_sonic_pre_pre.mp3")]
        public void OrdinaryUrlsAreNotClassifiedAsSequentialPreRoll(string url) =>
            Assert.False(TuneInDirectory.IsProviderPreRoll(url));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MissingLiveOrMisplacedPreRollFailsBeforeOpeningAudio(bool misplaced)
        {
            var directory = Directory(misplaced ? Live + "\n" + PreRoll + "\n" + Live : PreRoll,
                _ => throw new Exception("Invalid sequences must not open audio."));
            await Assert.ThrowsAsync<InvalidDataException>(() => directory.OpenStreamAsync(directory.Snapshot(),
                Station, CancellationToken.None, _ => { }, (source, _) => new OwnedStream(source)));
        }

        [Fact]
        public async Task DecoderStartupFailureReleasesSourceAndDoesNotSkipAd()
        {
            HttpStream ad = null;
            var directory = Directory(PreRoll + "\n" + Live,
                _ => ad = Reply("audio/mpeg", new byte[] { 1 }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => directory.OpenStreamAsync(directory.Snapshot(),
                Station, CancellationToken.None, _ => { },
                (_, __) => throw new InvalidOperationException("Decoder unavailable.")));
            Assert.True(ad.Disposed);
        }

        [Fact]
        public async Task LiveReadFailureAfterAdIsTerminalRatherThanASequenceOfLiveAlternatives()
        {
            var opened = 0;
            var directory = Directory(PreRoll + "\n" + Live + "\nhttps://radio.test/unused", uri =>
            {
                opened++;
                return uri.Host == "radio.test"
                    ? new HttpStream("HTTP/1.1 200 OK\r\nContent-Type: audio/aac\r\nContent-Length: 100\r\n\r\nLIVE")
                    : Reply("audio/mpeg", Encoding.ASCII.GetBytes("AD"));
            });
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => { }, (source, _) => new OwnedStream(source)))
                await Assert.ThrowsAsync<EndOfStreamException>(() =>
                    TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None));
            Assert.Equal(2, opened);
        }

        [Fact]
        public async Task PrivateLiveDnsAfterAdStillFailsClosedWithoutTryingAnotherAlternative()
        {
            var opened = 0;
            var directory = new TuneInDirectory(() => Config(), transport: new SafeHttpTransport(
                host => Task.FromResult(new[] { host == "radio.test" ? IPAddress.Loopback : IPAddress.Parse("8.8.8.8") }),
                (_, uri, __) =>
                {
                    opened++;
                    return Task.FromResult<Stream>(uri.Host == "opml.radiotime.com"
                        ? Reply("text/plain", Encoding.ASCII.GetBytes(PreRoll + "\n" + Live + "\nhttps://other.test/live"))
                        : Reply("audio/mpeg", Encoding.ASCII.GetBytes("AD")));
                }));
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => throw new Exception("Security rejections must not fall back."), (source, _) => new OwnedStream(source)))
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None));
            Assert.Equal(2, opened);
        }

        [FfmpegFact]
        public async Task RealMp3AdAndAacLiveDecodeToOneProbeablePcmStream()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            var mp3 = await EncodeAsync(encoder, "-f lavfi -i sine=frequency=440:duration=0.2 -ar 22050 -ac 1 -f mp3 pipe:1");
            var aac = await EncodeAsync(encoder, "-f lavfi -i sine=frequency=880:duration=0.2 -ar 48000 -ac 2 -f adts pipe:1");
            var directory = Directory(PreRoll + "\n" + Live, uri =>
                Reply(uri.Host == "radio.test" ? "audio/aac" : "audio/mpeg", uri.Host == "radio.test" ? aac : mp3));
            using (var probe = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => { }, (source, token) => new PcmDecoderStream(encoder, source, token)))
            {
                Assert.Equal("audio/wav", probe.ContentType);
                var header = new byte[44];
                Assert.Equal(44, await probe.Body.ReadAsync(header, 0, header.Length, CancellationToken.None));
                Assert.Equal("WAVE", Encoding.ASCII.GetString(header, 8, 4));
            }
            byte[] wave;
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(), Station, CancellationToken.None,
                _ => { }, (source, token) => new PcmDecoderStream(encoder, source, token)))
                wave = await TuneInDirectory.ReadBoundedAsync(response.Body, 1024 * 1024, CancellationToken.None);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
            Assert.True(wave.Length > 48000);
            Assert.Equal(0, (wave.Length - 44) % 4);
            byte[] adPcm;
            byte[] livePcm;
            using (var ad = new PcmDecoderStream(encoder,
                new SafeResponse(new MemoryStream(mp3), "audio/mpeg"), CancellationToken.None))
                adPcm = await TuneInDirectory.ReadBoundedAsync(ad, 1024 * 1024, CancellationToken.None);
            using (var live = new PcmDecoderStream(encoder,
                new SafeResponse(new MemoryStream(aac), "audio/aac"), CancellationToken.None))
                livePcm = await TuneInDirectory.ReadBoundedAsync(live, 1024 * 1024, CancellationToken.None);
            Assert.Equal(adPcm.Concat(livePcm), wave.Skip(44));
            // Re-probe the whole relay using the production decoder, not a byte-concatenation proxy.
            using (var decoded = new PcmDecoderStream(encoder,
                new SafeResponse(new MemoryStream(wave), "audio/wav"), CancellationToken.None))
                Assert.Equal(wave.Skip(44), await TuneInDirectory.ReadBoundedAsync(decoded, 1024 * 1024, CancellationToken.None));
        }

        [FfmpegFact]
        public async Task RealDecoderRejectsInvalidAudioAndDisposesCancelledProcesses()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            using (var invalid = new PcmDecoderStream(encoder,
                new SafeResponse(new MemoryStream(new byte[128]), "audio/mpeg"), CancellationToken.None))
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    TuneInDirectory.ReadBoundedAsync(invalid, 1000, CancellationToken.None));
            using (var token = new CancellationTokenSource())
            using (var waiting = new PcmDecoderStream(encoder,
                new SafeResponse(new WaitingStream(), "audio/aac"), token.Token))
            {
                var reading = waiting.ReadAsync(new byte[1024], 0, 1024, token.Token);
                token.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            }
        }

        [FfmpegTheory]
        [Trait("Category", "Live")]
        [InlineData("s6031")]
        [InlineData("s31466")]
        [InlineData("s141866")]
        [InlineData("s301791")]
        public async Task LiveProviderPreRollHandsOffThroughPinnedTransportAndActualDecoder(string station)
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            var directory = new TuneInDirectory(() => Config());
            var stages = 0;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(),
                new NavigationEntry { Category = "talk", StationId = station }, timeout.Token,
                _ => { }, (source, token) =>
                {
                    Interlocked.Increment(ref stages);
                    return new PcmDecoderStream(encoder, source, token);
                }))
            {
                Assert.Equal("audio/wav", response.ContentType);
                var buffer = new byte[19200];
                var liveBytes = 0;
                while (liveBytes < 192000)
                {
                    var size = await response.Body.ReadAsync(buffer, 0, buffer.Length, timeout.Token);
                    Assert.True(size > 0, "Relay ended before live music.");
                    if (Volatile.Read(ref stages) >= 2) liveBytes += size;
                }
                Assert.Equal(2, stages);
            }
        }

        [FfmpegFact]
        public async Task HostRelayUsesConfiguredDecoderAndReleasesItOnFactoryFailure()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            var audio = await EncodeAsync(encoder, "-f lavfi -i sine=duration=0.1 -f mp3 pipe:1");
            foreach (var factoryFails in new[] { false, true })
            {
                var sources = new List<HttpStream>();
                var directory = Directory(PreRoll + "\n" + Live, _ =>
                {
                    var source = Reply("audio/mpeg", audio);
                    sources.Add(source);
                    return source;
                });
                var id = directory.Register(directory.Snapshot(), Station);
                var factory = DispatchProxy.Create<IHttpResultFactory, TransportTests.RelayFactoryProxy>();
                var capture = (TransportTests.RelayFactoryProxy)(object)factory;
                capture.Fail = factoryFails;
                var service = new TuneInService(
                    DispatchProxy.Create<ILogManager, TransportTests.RelayRequestProxy>(), null, factory, directory,
                    DispatchProxy.Create<IFfmpegManager, EncoderProxy>())
                { Request = DispatchProxy.Create<IRequest, TransportTests.RelayRequestProxy>() };
                if (factoryFails)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => service.Get(new GetTuneInRelay { Id = id }));
                else
                    using (var result = Assert.IsAssignableFrom<Stream>(await service.Get(new GetTuneInRelay { Id = id })))
                    {
                        Assert.Equal("audio/wav", capture.ContentType);
                        var bytes = await TuneInDirectory.ReadBoundedAsync(result, 1024 * 1024, CancellationToken.None);
                        Assert.True(bytes.Length > 44);
                        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
                    }
                Assert.All(sources, source => Assert.True(source.Disposed));
                Assert.Equal(factoryFails ? 1 : 2, sources.Count);
            }
        }

        public class EncoderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                switch (method.Name)
                {
                    case "get_FfmpegConfiguration": return DispatchProxy.Create<IFfmpegConfiguration, EncoderProxy>();
                    case "get_EncoderPath": return Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
                    case "get_EnvironmentVariables": return Array.Empty<EnvironmentVariableSetting>();
                    default: throw new InvalidOperationException("Unexpected encoder dependency access.");
                }
            }
        }

        private static async Task<byte[]> EncodeAsync(string encoder, string arguments)
        {
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = encoder,
                Arguments = "-hide_banner -loglevel error " + arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                var errors = process.StandardError.ReadToEndAsync();
                var bytes = await TuneInDirectory.ReadBoundedAsync(process.StandardOutput.BaseStream, 1024 * 1024, CancellationToken.None);
                process.WaitForExit();
                Assert.Equal("", await errors);
                Assert.Equal(0, process.ExitCode);
                return bytes;
            }
        }

        private static PluginConfiguration Config() => new PluginConfiguration
        { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };

        private static TuneInDirectory Directory(string playlist, Func<Uri, HttpStream> open,
            PluginConfiguration config = null)
        {
            var saved = config ?? Config();
            return new TuneInDirectory(() => saved, transport: new SafeHttpTransport(
                _ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, uri, __) => Task.FromResult<Stream>(uri.Host == "opml.radiotime.com"
                    ? Reply("text/plain", Encoding.ASCII.GetBytes(playlist)) : open(uri))));
        }

        private static HttpStream Reply(string type, byte[] body, int status = 200) =>
            new HttpStream(Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Status\r\nContent-Type: " + type
                + "\r\nContent-Length: " + body.Length + "\r\n\r\n").Concat(body).ToArray());

        private sealed class HttpStream : MemoryStream
        {
            public bool Disposed { get; private set; }
            public HttpStream(byte[] bytes) : base(bytes) { }
            public HttpStream(string text) : this(Encoding.ASCII.GetBytes(text)) { }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => Task.CompletedTask;
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        private sealed class OwnedStream : RadioReadStream
        {
            private readonly SafeResponse source;
            public OwnedStream(SafeResponse source) { this.source = source; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                source.Body.ReadAsync(buffer, offset, count, token);
            protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        }

        private sealed class WaitingStream : RadioReadStream
        {
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
        }

        public sealed class FfmpegFactAttribute : FactAttribute
        {
            public FfmpegFactAttribute()
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG")))
                    Skip = "Set TUNEIN_TEST_FFMPEG to run real codec/process regression coverage.";
                Timeout = 30000;
            }
        }

        public sealed class FfmpegTheoryAttribute : TheoryAttribute
        {
            public FfmpegTheoryAttribute()
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG")))
                    Skip = "Set TUNEIN_TEST_FFMPEG to run real codec/process regression coverage.";
                Timeout = 150000;
            }
        }
    }
}
