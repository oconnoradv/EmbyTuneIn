using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using MediaBrowser.Plugins.TuneIn.Configuration;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class TransportTests
    {
        [Fact]
        [Trait("Category", "Live")]
        public async Task LiveCatalogueWorksThroughPinnedDnsAndVerifiedTls()
        {
            var transport = new SafeHttpTransport();
            int? receivedStatus = null;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var response = await transport.OpenAsync(
                "https://opml.radiotime.com/Browse.ashx?c=lang", true, timeout.Token, status => receivedStatus = status))
            {
                var bytes = await TuneInDirectory.ReadBoundedAsync(response.Body, 2 * 1024 * 1024, timeout.Token);
                var document = TuneInDirectory.ParseCatalogue(bytes);
                Assert.NotEmpty(document.Descendants("outline"));
                Assert.Equal(200, receivedStatus);
                Assert.DoesNotContain(document.Descendants("outline"), row => TuneInDirectory.Attribute(row, "type") == "audio");
            }
        }

        [Theory]
        [InlineData("127.0.0.1", false)]
        [InlineData("10.1.2.3", false)]
        [InlineData("172.31.1.1", false)]
        [InlineData("192.168.1.1", false)]
        [InlineData("169.254.169.254", false)]
        [InlineData("100.64.1.1", false)]
        [InlineData("198.18.0.1", false)]
        [InlineData("192.0.2.1", false)]
        [InlineData("224.0.0.1", false)]
        [InlineData("::1", false)]
        [InlineData("::ffff:127.0.0.1", false)]
        [InlineData("fe80::1", false)]
        [InlineData("fc00::1", false)]
        [InlineData("2001:db8::1", false)]
        [InlineData("2002:7f00:1::", false)]
        [InlineData("8.8.8.8", true)]
        [InlineData("2606:4700:4700::1111", true)]
        public void SpecialUseDestinationsAreRejected(string address, bool isPublic) =>
            Assert.Equal(isPublic, PublicAddressPolicy.IsPublic(IPAddress.Parse(address)));

        [Theory]
        [InlineData("http://opml.radiotime.com/Browse.ashx?id=r0")]
        [InlineData("https://opml.radiotime.com:444/Browse.ashx?id=r0")]
        [InlineData("https://evil.test/Browse.ashx?id=r0")]
        [InlineData("https://opml.radiotime.com/Browse.ashx/evil")]
        [InlineData("https://user:password@opml.radiotime.com/Browse.ashx")]
        public void CatalogueNetworkPolicyIsHttpsExactHostAndExactPath(string url) =>
            Assert.Throws<InvalidOperationException>(() => PublicAddressPolicy.ValidateUri(url, true));

        [Fact]
        public async Task MixedPublicAndPrivateDnsFailsBeforeConnecting()
        {
            var connections = 0;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
                (_, __, ___) => { connections++; throw new Exception("Must not connect"); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
            Assert.Equal(0, connections);
        }

        [Fact]
        public async Task RedirectToPrivateHostIsRejectedBeforeSecondConnection()
        {
            var connections = 0;
            var transport = new SafeHttpTransport(host => Task.FromResult(new[] {
                host == "radio.test" ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback }),
                (_, __, ___) =>
                {
                    connections++;
                    return Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 302 Found\r\nLocation: https://127.0.0.1/secret\r\n\r\n"));
                });
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
            Assert.Equal(1, connections);
        }

        [Fact]
        public async Task TlsDowngradeAndCatalogueCrossHostRedirectsFailClosed()
        {
            foreach (var destination in new[] { "http://opml.radiotime.com/Browse.ashx", "https://evil.test/Browse.ashx" })
            {
                var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                    (_, __, ___) => Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 302 Found\r\nLocation: " + destination + "\r\n\r\n")));
                await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync(
                    "https://opml.radiotime.com/Browse.ashx?id=r0", true, CancellationToken.None));
            }
        }

        [Fact]
        public async Task AddressIsPinnedAndPathCasePreservedAndChunkedBodyDecoded()
        {
            var lookups = 0;
            DuplexStream connection = null;
            var transport = new SafeHttpTransport(_ =>
            {
                lookups++;
                return Task.FromResult(new[] { lookups == 1 ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback });
            }, (address, uri, token) =>
            {
                Assert.Equal("8.8.8.8", address.ToString());
                Assert.Equal("radio.test", uri.DnsSafeHost);
                connection = new DuplexStream("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: audio/mpeg\r\n\r\n4\r\nTest\r\n3\r\n123\r\n0\r\n\r\n");
                return Task.FromResult<Stream>(connection);
            });
            using (var response = await transport.OpenAsync("https://radio.test/CaseSensitive?Token=AbC", false, CancellationToken.None))
            {
                var body = await TuneInDirectory.ReadBoundedAsync(response.Body, 100, CancellationToken.None);
                Assert.Equal("Test123", Encoding.ASCII.GetString(body));
                Assert.Contains("GET /CaseSensitive?Token=AbC HTTP/1.1", connection.Request);
                Assert.Contains("Host: radio.test", connection.Request);
            }
            Assert.Equal(1, lookups);
        }

        [Fact]
        public async Task TruncatedAndOversizedResponsesAreErrorsNotSuccessFallbacks()
        {
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) => Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nshort")));
            using (var response = await transport.OpenAsync("https://radio.test/live", false, CancellationToken.None))
                await Assert.ThrowsAsync<EndOfStreamException>(() => TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => TuneInDirectory.ReadBoundedAsync(new MemoryStream(new byte[20]), 10, CancellationToken.None));
        }

        [Fact]
        public void PlaylistParsingPreservesCaseSkipsCommentsAndIndexesPlsCorrectly()
        {
            var urls = TuneInDirectory.PlaylistUrls("[playlist]\nNumberOfEntries=2\nFile1=https://radio.test/CaseA\nFile2=https://radio.test/CaseB\n#comment").ToArray();
            Assert.Equal(new[] { "https://radio.test/CaseA", "https://radio.test/CaseB" }, urls);
            Assert.Throws<InvalidDataException>(() => TuneInDirectory.PlaylistUrls(
                "https://cdn-cms.tunein.com/service/Audio/nostream.enUS.mp3").ToArray());
        }

        [Fact]
        public async Task PlsAlternativesAreTriedWithExplicitWarningsAndNoRemoteUrlHandoff()
        {
            var requests = 0;
            var warnings = 0;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                (_, uri, __) =>
                {
                    requests++;
                    if (uri.Host == "opml.radiotime.com") return Task.FromResult<Stream>(Reply("text/plain", "https://radio.test/playlist.pls"));
                    if (uri.AbsolutePath == "/playlist.pls") return Task.FromResult<Stream>(Reply("audio/x-scpls",
                        "[playlist]\nFile1=https://radio.test/dead\nFile2=https://radio.test/CaseLive"));
                    if (uri.AbsolutePath == "/dead") return Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 503 Unavailable\r\n\r\n"));
                    Assert.Equal("/CaseLive", uri.AbsolutePath);
                    return Task.FromResult<Stream>(Reply("audio/mpeg", "synthetic-test-audio"));
                });
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, transport: transport);
            using (var response = await directory.OpenStreamAsync(directory.Snapshot(),
                new NavigationEntry { Category = "talk", StationId = "s1" }, CancellationToken.None, message =>
                {
                    warnings++;
                    Assert.DoesNotContain("https://", message);
                }))
                Assert.Equal("synthetic-test-audio", Encoding.UTF8.GetString(
                    await TuneInDirectory.ReadBoundedAsync(response.Body, 1000, CancellationToken.None)));
            Assert.Equal(4, requests);
            Assert.Equal(1, warnings);
        }

        [Fact]
        public async Task PrivateAlternativeIsRejectedRatherThanSilentlyFallingBack()
        {
            var requests = 0;
            var transport = new SafeHttpTransport(host => Task.FromResult(new[] {
                host == "127.0.0.1" ? IPAddress.Loopback : IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) =>
                {
                    requests++;
                    return Task.FromResult<Stream>(Reply("text/plain", "https://127.0.0.1/secret\nhttps://radio.test/live"));
                });
            var config = new PluginConfiguration { SchemaVersion = 1, ConfigurationCompleted = true, EnableTalk = true };
            var directory = new TuneInDirectory(() => config, transport: transport);
            await Assert.ThrowsAsync<InvalidOperationException>(() => directory.OpenStreamAsync(directory.Snapshot(),
                new NavigationEntry { Category = "talk", StationId = "s1" }, CancellationToken.None, _ => throw new Exception("No silent security fallback")));
            Assert.Equal(1, requests);
        }

        private static Stream Reply(string type, string body) => new DuplexStream(
            "HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nContent-Length: "
            + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);

        [Fact]
        public async Task UnconfiguredPlaybackMakesZeroRequests()
        {
            var requests = 0;
            var transport = new SafeHttpTransport(_ =>
            {
                requests++;
                throw new Exception("Playback must not reach DNS before setup.");
            });
            var config = new PluginConfiguration();
            var directory = new TuneInDirectory(() => config, transport: transport);
            await Assert.ThrowsAsync<InvalidOperationException>(() => directory.OpenStreamAsync(directory.Snapshot(),
                new NavigationEntry { Category = "talk", StationId = "s1" }, CancellationToken.None, _ => { }));
            Assert.Equal(0, requests);
        }

        private sealed class DuplexStream : MemoryStream
        {
            private readonly MemoryStream output = new MemoryStream();
            public DuplexStream(string response) : base(Encoding.ASCII.GetBytes(response)) { }
            public string Request => Encoding.ASCII.GetString(output.ToArray());
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                output.WriteAsync(buffer, offset, count, token);
        }
    }
}
